"""``python -m aerie_trading.collect`` - the surface a CronJob actually calls.

The container's ``command:`` is the contract between
``deploy/cluster/trading/lake/`` and this package, and it is the kind of
contract that is discovered to be wrong at 21:30 on a weekday. So the argument
grammar is tested, and so is the exit code that matters most: **2 for a
refusal**, which is what stops a Job that fired on a holiday from advancing the
last-success metric over a day nothing was collected.

Only the paths that need no database are exercised here. A collection writes an
``ingest_run`` row before it does anything, so anything past a refusal needs a
Postgres - which is what ``tests/test_collect_bars.py`` and its fake run log
are for.
"""

from collections.abc import Generator
from pathlib import Path

import pytest

from aerie_trading.collect.__main__ import EXIT_REFUSED, build_parser, build_provider, main
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.settings import Settings, get_settings

GOOD_FRIDAY = "2026-04-03"


@pytest.fixture(autouse=True)
def isolated_settings(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Generator[None]:
    """A lake in a temporary directory, and no cached settings either side.

    ``get_settings`` is deliberately cached for the life of a process, and
    ``main`` calls it - so a test that changed the environment without clearing
    it would either read another test's values or leave its own behind for the
    next one.
    """
    monkeypatch.setenv("TRADING_LAKE_ROOT", str(tmp_path / "lake"))
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


def test_a_collector_must_be_named() -> None:
    """No default subcommand: a typo'd CronJob argument must not silently
    collect the wrong thing on a schedule."""
    with pytest.raises(SystemExit):
        build_parser().parse_args([])


def test_the_arguments_the_cronjobs_pass_are_understood() -> None:
    """Every invocation in deploy/cluster/trading/lake/, parsed here."""
    bars = build_parser().parse_args(["bars", "--mode", "incremental"])
    assert (bars.collector, bars.mode, bars.intervals) == ("bars", "incremental", None)

    chains = build_parser().parse_args(["chains"])
    assert (chains.collector, chains.at, chains.session) == ("chains", None, None)

    backfill = build_parser().parse_args(
        ["bars", "--mode", "backfill", "--interval", "1d", "--start", "2026-01-02"]
    )
    assert backfill.intervals == ["1d"]
    assert backfill.start.isoformat() == "2026-01-02"


def test_a_bar_collection_on_a_holiday_exits_refused_not_failed() -> None:
    """The after-the-close CronJob's own holiday behaviour.

    ``--mode incremental`` means *today's* session, so a run that fires on Good
    Friday refuses. The catch-up mode (``--mode latest``) is the one that skips
    back to the previous session, and it is deliberately not what the schedule
    uses.
    """
    assert main(["bars", "--mode", "incremental", "--on", GOOD_FRIDAY]) == EXIT_REFUSED


def test_an_unknown_interval_is_refused_by_the_parser() -> None:
    with pytest.raises(SystemExit):
        build_parser().parse_args(["bars", "--interval", "4h"])


def test_a_chain_collection_on_a_holiday_exits_refused_not_failed() -> None:
    """The exit code the plan's schedule design rests on.

    0 would advance ``kube_cronjob_status_last_successful_time`` over a day
    nothing was collected; 1 would page someone about Good Friday. 2 is a
    failed Job that says which of the two it is, and the CronJob carries
    ``backoffLimit: 0`` because retrying cannot make the market open.
    """
    assert main(["chains", "--session", GOOD_FRIDAY]) == EXIT_REFUSED


def test_a_naive_instant_is_refused_before_anything_is_collected() -> None:
    """``--at`` without an offset is ambiguous, and the ambiguity is five hours
    wide for the exchange this answers to."""
    with pytest.raises(SystemExit, match="timezone-aware"):
        main(["chains", "--at", "2026-03-04T15:00:00"])


def test_the_provider_is_built_from_the_configured_universe() -> None:
    """The composition root, and the only line Phase 8 adds to this package.

    Asserting that the *settings'* configuration reaches the provider is what
    makes "re-seed with an environment variable, no rebuild" true rather than
    claimed.
    """
    settings = Settings(synthetic=SyntheticConfig(seed=7))
    provider = build_provider(settings)

    assert isinstance(provider, SyntheticMarketDataProvider)
    assert provider.config.seed == 7
    # And it reaches the provenance blob, which is what a run is reproducible
    # from - a provider built with the right seed but registering the default
    # one would be the worst of both.
    assert provider.provenance["config"] == settings.synthetic.model_dump(mode="json")
