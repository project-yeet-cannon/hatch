"""``python -m aerie_trading.runs``, on the paths that need no database.

The commands that write need a Postgres and are covered by
``tests/test_run_queue.py`` and ``tests/test_worker.py``. What is asserted here
is the part of the launcher that has to work when the database is the thing
that is wrong: an estimate, and a refusal.

That second one is the point of this file existing. *"Sweep size is estimated
and confirmed before enqueueing"* is only a guarantee if the refusal happens
before anything is opened, connected to or written - so these tests run with no
database configured at all, and a refusal that reached for one would fail here
with a connection error rather than an exit code.
"""

from __future__ import annotations

import pytest

from aerie_trading.runs.__main__ import EXIT_REFUSED, main

COMMON = [
    "--strategy",
    "ma_crossover",
    "--symbol",
    "ZVZZT",
    "--start",
    "2024-01-01",
    "--end",
    "2025-01-01",
]


def test_plan_prints_the_estimate_and_writes_nothing(capsys: pytest.CaptureFixture[str]) -> None:
    assert main(["plan", *COMMON, "--set", "fast=5,10", "--set", "slow=20,40"]) == 0

    printed = capsys.readouterr().out
    assert "4 run(s) of ma_crossover" in printed
    assert "Confirm with --confirm 4" in printed


def test_plan_reports_the_corner_the_strategy_pruned(
    capsys: pytest.CaptureFixture[str],
) -> None:
    assert main(["plan", *COMMON, "--set", "fast=5,20", "--set", "slow=20,40"]) == 0

    printed = capsys.readouterr().out
    assert "3 run(s)" in printed
    assert "1 pruned" in printed


def test_planning_over_a_declared_range_uses_the_range_the_model_declares(
    capsys: pytest.CaptureFixture[str],
) -> None:
    # --sweep rather than --set: no numbers on the command line at all, which
    # is the form that cannot disagree with the strategy's own declaration.
    assert main(["plan", *COMMON, "--sweep", "fast", "--sweep", "slow"]) == 0

    # 49 fast values by 40 slow ones is 1,960 combinations, of which the ones
    # with fast >= slow are pruned.
    printed = capsys.readouterr().out
    assert "pruned" in printed
    assert "1960" not in printed


def test_enqueueing_with_the_wrong_count_is_refused_before_a_database_is_opened(
    capsys: pytest.CaptureFixture[str],
) -> None:
    # No Postgres is configured in this suite, so reaching for one here would
    # raise rather than return. The exit code is the assertion.
    code = main(["enqueue", *COMMON, "--set", "fast=5,10", "--set", "slow=20,40", "--confirm", "7"])

    assert code == EXIT_REFUSED
    assert "4 runs and 7 were confirmed" in capsys.readouterr().out


def test_enqueueing_above_the_ceiling_is_refused_before_a_database_is_opened(
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TRADING_RUNS__MAX_SWEEP_RUNS", "2")
    from aerie_trading.settings import get_settings

    get_settings.cache_clear()
    try:
        code = main(
            ["enqueue", *COMMON, "--set", "fast=5,10", "--set", "slow=20,40", "--confirm", "4"]
        )
    finally:
        get_settings.cache_clear()

    assert code == EXIT_REFUSED
    assert "the ceiling is 2" in capsys.readouterr().out


def test_the_demo_can_be_previewed_without_writing_anything(
    capsys: pytest.CaptureFixture[str],
) -> None:
    assert main(["demo", "--dry-run"]) == 0

    printed = capsys.readouterr().out
    assert "demo-ma-crossover: 19 run(s)" in printed
    assert "demo-buy-and-hold: 1 run(s)" in printed


def test_enqueue_without_a_strategy_says_so_rather_than_expanding_nothing() -> None:
    with pytest.raises(SystemExit, match="--strategy is required"):
        main(["enqueue", "--confirm", "1"])


def test_enqueue_without_a_window_says_so() -> None:
    with pytest.raises(SystemExit, match="--start and --end are required"):
        main(["enqueue", "--strategy", "buy_and_hold", "--confirm", "1"])


def test_a_malformed_set_is_refused_with_the_shape_it_wanted() -> None:
    with pytest.raises(SystemExit, match="wants PARAM=V1,V2"):
        main(["plan", *COMMON, "--set", "fast"])


def test_a_non_numeric_set_is_refused() -> None:
    with pytest.raises(SystemExit, match="wants numbers"):
        main(["plan", *COMMON, "--set", "fast=quick"])


def test_naming_a_strategy_this_build_does_not_ship_lists_the_ones_it_does() -> None:
    with pytest.raises(LookupError, match="it ships"):
        main(["plan", "--strategy", "nope", "--start", "2024-01-01", "--end", "2025-01-01"])
