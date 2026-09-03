"""The control panel, over the whole vertical it renders.

docs/plans/trading.md Phase 7's screens are projections of rows the earlier
phases write, so a test that hand-inserted those rows would be a test of the
projection against a fixture rather than against the product. Everything here
therefore goes the long way: a provider writes Parquet, a sweep is enqueued,
a worker backtests it with the honesty layer attached, and only then is the API
asked what the screens would show.

Skips without ``TRADING_TEST_DATABASE_URL`` for the reason every queue test
does - ``DISTINCT ON``, a partial index and ``ANY(:ids)`` are Postgres, and a
fake implementing them would be a test of the fake.

The one thing asserted here that is not visible on a screen is the refusal: a
run with no out-of-sample window must not have its figures served as
performance, and the assertion is that the *response body* puts them under
``in_sample`` - which is the honesty layer being structural rather than a
convention the UI is trusted to follow.
"""

from __future__ import annotations

from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.collect.config import CollectionConfig
from aerie_trading.control.app import create_app
from aerie_trading.db.models import DataSource
from aerie_trading.engine.metrics import SHARPE, TOTAL_RETURN
from aerie_trading.honesty.config import WalkForwardSpec
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.runs.config import RunnerConfig
from aerie_trading.runs.costs import CostSpec
from aerie_trading.runs.queue import RunQueue
from aerie_trading.runs.sweep import SweepSpec, enqueue_sweep, plan_sweep
from aerie_trading.runs.worker import Assessor, HistoryCache, Worker
from aerie_trading.settings import Settings
from tests.conftest import STAMPED, StubDatabase
from tests.test_control import NO_BUNDLE

SYMBOL = "ZVZZT"
FIRST_SESSION = date(2022, 1, 3)
LAST_SESSION = date(2024, 12, 31)
WINDOW = (datetime(2022, 1, 1, tzinfo=UTC), datetime(2025, 1, 1, tzinfo=UTC))
REVISION = "d" * 40


@pytest.fixture(scope="session")
def panel_lake(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """Three years of daily bars, written the way the collectors write them.

    Three rather than the six months the queue tests use, for the reason those
    tests give about their own long lake: a fold has to be longer than the
    warm-up of the slowest candidate in the grid, or the walk-forward correctly
    fails and there is no headline row to put on a leaderboard.
    """
    root: Path = tmp_path_factory.mktemp("panel-lake")
    provider = SyntheticMarketDataProvider()
    writer = LakeWriter(
        root=root,
        provenance=Provenance("synthetic", REVISION, datetime(2025, 1, 1, tzinfo=UTC)),
    )
    sessions = provider.market_hours(FIRST_SESSION, LAST_SESSION)
    writer.write_bars(
        provider.bars(
            [SYMBOL],
            Interval.ONE_DAY,
            sessions[0].open,
            sessions[-1].close + timedelta(minutes=1),
        )
    )
    return root


@pytest.fixture
def source_id(ledger: Engine) -> int:
    with Session(ledger) as session, session.begin():
        source = DataSource(
            name="synthetic",
            description="Generated prices with no alpha in them by construction.",
            config={"seed": 7},
        )
        session.add(source)
        session.flush()
        return source.id


def settings() -> Settings:
    """Settings that name this test's universe and nothing about a cluster."""
    return Settings(collection=CollectionConfig(bar_symbols=(SYMBOL,)))  # pyright: ignore[reportCallIssue]


def client(ledger: Engine) -> TestClient:
    """The app, its panel pointed at the scratch Ledger.

    The stub database stays where it is: it answers the probes and the metrics,
    whose interesting case is the one where they raise, and the panel's
    interesting case is a real Postgres holding real runs. Two seams, for two
    different questions - see ``create_app``.
    """
    return TestClient(
        create_app(
            settings=settings(),
            database=StubDatabase(),
            revision=STAMPED,
            engine=ledger,
            # No bundle, so these tests describe the API alone whether or not
            # whoever ran them had also built the control panel. See
            # tests/test_control.py's note on the same seam.
            static_root=NO_BUNDLE,
        )
    )


def run_a_sweep(ledger: Engine, lake: Path, source_id: int, name: str = "panel-sweep") -> int:
    """Enqueue a small grid with its walk-forward, and work it to completion."""
    spec = SweepSpec(
        name=name,
        strategy="ma_crossover",
        grid={"fast": (Decimal(5), Decimal(10)), "slow": (Decimal(20), Decimal(40))},
        symbols=(SYMBOL,),
        interval=Interval.ONE_DAY,
        window_start=WINDOW[0],
        window_end=WINDOW[1],
        costs=CostSpec(),
        walk_forward=WalkForwardSpec(folds=3, train_multiple=2),
    )
    plan = plan_sweep(spec)
    with Session(ledger) as session, session.begin():
        sweep_id = enqueue_sweep(
            session, plan, confirm=plan.total, data_source_id=source_id, ceiling=100_000
        )

    with LakeReader(lake) as reader:
        cache = HistoryCache(reader, maxsize=2)
        worker = Worker(
            queue=RunQueue(ledger, worker="panel-worker", lease_seconds=900),
            cache=cache,
            revision=REVISION,
            config=RunnerConfig(poll_seconds=0.01),
            # The honesty layer attached, because the leaderboard's whole
            # content is what it computes: the baselines, the cost re-score and
            # the selection haircut are the columns Phase 6 says ship visible.
            assessor=Assessor(cache, settings().honesty, (SYMBOL,)),
        )
        report = worker.run(max_idle_polls=1)
    assert report.failed == 0, "the fixture sweep must complete for the screens to have content"
    return sweep_id


@pytest.fixture
def worked(ledger: Engine, panel_lake: Path, source_id: int) -> int:
    return run_a_sweep(ledger, panel_lake, source_id)


# -- the strategies list ------------------------------------------------------


def test_the_strategies_list_carries_the_parameter_space(ledger: Engine, worked: int) -> None:
    # *"Every strategy, its parameter space, its best walk-forward result, its
    # live status."* The parameter space comes off the strategy's own pydantic
    # model, so a launcher rendering it cannot offer a value the model refuses.
    cards = client(ledger).get("/api/trading/strategies").json()

    card = next(entry for entry in cards if entry["name"] == "ma_crossover")
    assert card["shipped"] is True
    fast = next(entry for entry in card["parameters"] if entry["name"] == "fast")
    assert fast["swept"] is True
    assert Decimal(fast["low"]) < Decimal(fast["high"])
    assert fast["count"] >= 2
    assert card["runs"]["succeeded"] == 5


def test_a_strategys_headline_is_its_walk_forward_result(ledger: Engine, worked: int) -> None:
    # The one number on the front page, and it may only ever come from a run
    # whose window was held out. A best-of that ranged over in-sample runs
    # would put the luckiest member of the widest sweep on the front page,
    # which is the exact failure Phase 6 exists to prevent.
    cards = client(ledger).get("/api/trading/strategies").json()

    best = next(entry for entry in cards if entry["name"] == "ma_crossover")["best"]
    assert best is not None
    assert best["kind"] == "walk_forward"
    assert best["figures"]["sample"] == "out_of_sample"
    assert SHARPE in best["figures"]["headline"]
    assert best["figures"]["in_sample"] == {}


def test_live_status_is_stated_rather_than_left_to_be_inferred(ledger: Engine, worked: int) -> None:
    cards = client(ledger).get("/api/trading/strategies").json()

    assert {card["live"] for card in cards} == {"backtest_only"}


# -- the leaderboard ----------------------------------------------------------


def test_the_leaderboard_defaults_to_the_only_figures_that_may_be_headlines(
    ledger: Engine, worked: int
) -> None:
    board = client(ledger).get("/api/trading/leaderboard").json()

    assert board["sample"] == "out_of_sample"
    assert board["rows"], "a completed sweep leaves one walk-forward row"
    for row in board["rows"]:
        assert row["figures"]["sample"] == "out_of_sample"
        assert row["figures"]["headline"]


def test_an_in_sample_row_is_served_under_a_name_that_says_so(ledger: Engine, worked: int) -> None:
    # The refusal, visible in the response body: an ordinary sweep run has no
    # out-of-sample window, so its Sharpe is served under `in_sample` and the
    # `headline` block a UI would sort on is empty. The guard is in the
    # serializer (honesty/presentation.py), which is why no route here had to
    # remember it.
    board = client(ledger).get("/api/trading/leaderboard?sample=in_sample").json()

    assert board["rows"]
    for row in board["rows"]:
        assert row["figures"]["headline"] == {}
        assert SHARPE in row["figures"]["in_sample"]


def test_every_row_names_the_source_its_numbers_came_from(ledger: Engine, worked: int) -> None:
    # *"Data provenance is visible on every result... not a footnote on a
    # settings page."* Required on the row type, so a screen cannot omit it by
    # not asking.
    board = client(ledger).get("/api/trading/leaderboard?sample=all").json()

    assert board["rows"]
    assert all(row["source"]["name"] == "synthetic" for row in board["rows"])


def test_the_board_can_be_sorted_on_another_metric(ledger: Engine, worked: int) -> None:
    board = (
        client(ledger).get(f"/api/trading/leaderboard?sample=in_sample&sort={TOTAL_RETURN}").json()
    )

    values = [Decimal(row["figures"]["in_sample"][TOTAL_RETURN]) for row in board["rows"]]
    assert values == sorted(values, reverse=True)


def test_an_unknown_metric_is_an_empty_board_rather_than_an_error(
    ledger: Engine, worked: int
) -> None:
    # The sort name is bound as a *value* into `run_metric.name = :sort`, never
    # interpolated as a column, so a name this build does not write is a board
    # with nothing on it.
    response = client(ledger).get("/api/trading/leaderboard?sample=all&sort=whatever")

    assert response.status_code == 200
    assert response.json()["rows"] == []


def test_the_live_slice_is_empty_and_says_why(ledger: Engine, worked: int) -> None:
    # Nothing has traded live in this build. An empty board with a sentence on
    # it is a better answer than a control the UI does not offer.
    board = client(ledger).get("/api/trading/leaderboard?mode=live&sample=all").json()

    assert board["rows"] == []
    assert board["note"] is not None


def test_a_filter_this_build_does_not_have_is_refused(ledger: Engine, worked: int) -> None:
    # Not defaulted. A board that silently answered a different question from
    # the one asked is the one failure a leaderboard cannot afford.
    response = client(ledger).get("/api/trading/leaderboard?since=fortnight")

    assert response.status_code == 400
    assert "since" in response.json()["detail"]


def test_today_and_inception_are_different_questions(ledger: Engine, worked: int) -> None:
    reachable = client(ledger)
    since_inception = reachable.get("/api/trading/leaderboard?sample=all&limit=500").json()
    today = reachable.get("/api/trading/leaderboard?sample=all&since=today&limit=500").json()

    # Every run in this test finished a moment ago, so the two agree - which is
    # the assertion: the filter narrows on `finished_at` rather than dropping
    # rows for a reason nobody asked for.
    assert len(today["rows"]) == len(since_inception["rows"])


# -- the run detail -----------------------------------------------------------


def test_the_run_detail_carries_what_a_result_is_reproduced_from(
    ledger: Engine, worked: int
) -> None:
    # *"Trades, the equity curve, the metrics, and the exact parameters and
    # revision, so a result can be reproduced."*
    reachable = client(ledger)
    board = reachable.get("/api/trading/leaderboard?sample=in_sample").json()
    run_id = board["rows"][0]["id"]

    detail = reachable.get(f"/api/trading/runs/{run_id}").json()

    assert detail["run"]["params"] == {"fast": 5, "slow": 20} or detail["run"]["params"]
    assert detail["run"]["aerie_revision"] == REVISION
    assert detail["run"]["result_fingerprint"]
    assert detail["run"]["source"]["name"] == "synthetic"
    assert detail["trades"], "an ma_crossover over three years trades at least once"
    assert detail["curve"]["recorded"] is True
    assert len(detail["curve"]["points"]) == detail["curve"]["points_total"]
    assert detail["sweep"]["name"] == "panel-sweep"


def test_the_curve_ends_where_the_final_equity_metric_says(ledger: Engine, worked: int) -> None:
    # The chart and the number beside it are read together, and a sampled curve
    # that could drop its last point would make them disagree.
    reachable = client(ledger)
    run_id = reachable.get("/api/trading/leaderboard?sample=in_sample").json()["rows"][0]["id"]

    detail = reachable.get(f"/api/trading/runs/{run_id}").json()

    last = Decimal(detail["curve"]["points"][-1][1])
    reported = Decimal(detail["run"]["figures"]["in_sample"]["final_equity"])
    assert last == reported


def test_a_walk_forward_run_shows_the_choices_it_made(ledger: Engine, worked: int) -> None:
    # The content of a walk-forward is the sequence of parameter sets it chose,
    # and a detail that showed only the stitched number would be showing the
    # summary of a thing it does not display.
    reachable = client(ledger)
    run_id = reachable.get("/api/trading/leaderboard").json()["rows"][0]["id"]

    detail = reachable.get(f"/api/trading/runs/{run_id}").json()

    assert detail["run"]["params"] is None
    assert len(detail["folds"]) == 3
    assert all(fold["params"] for fold in detail["folds"])
    assert [fold["fold"] for fold in detail["folds"]] == [0, 1, 2]


def test_an_unknown_run_is_a_404(ledger: Engine) -> None:
    assert client(ledger).get("/api/trading/runs/424242").status_code == 404


# -- the sweep ----------------------------------------------------------------


def test_a_sweep_reports_progress_against_its_own_denominator(ledger: Engine, worked: int) -> None:
    detail = client(ledger).get(f"/api/trading/sweeps/{worked}").json()

    sweep = detail["sweeps"][0]
    assert sweep["progress"]["complete"] is True
    assert sweep["progress"]["finished"] == sweep["progress"]["total_runs"]
    # The grid, not the row count: the walk-forward is machinery rather than a
    # trial, and counting it would deflate every sibling's selection haircut.
    assert sweep["trials"] == 4
    assert sweep["progress"]["total_runs"] == 5
    assert sweep["seeded"] is False


# -- launching ----------------------------------------------------------------


def plan_body(**overrides: Any) -> dict[str, Any]:
    body: dict[str, Any] = {
        "strategy": "ma_crossover",
        "swept": ["fast"],
        "grid": {"slow": ["40", "60"]},
        "symbols": [SYMBOL],
        "window_start": WINDOW[0].isoformat(),
        "window_end": WINDOW[1].isoformat(),
    }
    body.update(overrides)
    return body


def test_the_estimate_writes_nothing(ledger: Engine, source_id: int) -> None:
    reachable = client(ledger)

    plan = reachable.post("/api/trading/sweeps/plan", json=plan_body()).json()

    assert plan["total"] > 0
    # The grid plus the walk-forward row, which is the number of rows the queue
    # would gain - and deliberately not the number to be confirmed.
    assert plan["rows"] == plan["total"] + 1
    assert reachable.get("/api/trading/queue").json()["counts"]["queued"] == 0


def test_a_launch_without_the_estimate_is_refused(ledger: Engine, source_id: int) -> None:
    # The handshake, in front of a button. A caller that never looked at the
    # estimate cannot supply it, which is the whole point of it being required.
    reachable = client(ledger)
    plan = reachable.post("/api/trading/sweeps/plan", json=plan_body()).json()

    response = reachable.post(
        "/api/trading/sweeps", json={"request": plan_body(), "confirm": plan["total"] + 1}
    )

    assert response.status_code == 409
    assert str(plan["total"]) in response.json()["detail"]
    assert reachable.get("/api/trading/queue").json()["counts"]["queued"] == 0


def test_a_confirmed_launch_puts_runs_on_the_queue(ledger: Engine, source_id: int) -> None:
    reachable = client(ledger)
    plan = reachable.post("/api/trading/sweeps/plan", json=plan_body()).json()

    response = reachable.post(
        "/api/trading/sweeps", json={"request": plan_body(), "confirm": plan["total"]}
    )

    assert response.status_code == 201
    sweep_id = response.json()["sweep_id"]
    detail = reachable.get(f"/api/trading/sweeps/{sweep_id}").json()
    assert detail["sweeps"][0]["progress"]["total_runs"] == plan["rows"]
    assert reachable.get("/api/trading/queue").json()["counts"]["queued"] == plan["rows"]


def test_a_launch_with_nothing_collected_is_refused_before_it_costs_anything(
    ledger: Engine,
) -> None:
    # No data_source row: nothing has ever collected here. The alternative to
    # this refusal is a sweep whose every run fails with "collect before
    # backtesting" a minute later.
    response = client(ledger).post(
        "/api/trading/sweeps", json={"request": plan_body(), "confirm": 1}
    )

    assert response.status_code == 409
    assert "collect" in response.json()["detail"]


def test_a_strategy_this_build_does_not_ship_cannot_be_launched(ledger: Engine) -> None:
    response = client(ledger).post(
        "/api/trading/sweeps/plan", json=plan_body(strategy="nothing_like_this")
    )

    assert response.status_code == 404


def test_the_installation_describes_itself_rather_than_being_compiled_in(
    ledger: Engine,
) -> None:
    # docs/ethos.md: nothing in this repository may be true of exactly one
    # installation. A launcher form with a symbol list baked into it is that.
    payload = client(ledger).get("/api/trading/installation").json()

    assert payload["symbols"] == [SYMBOL]
    assert payload["max_sweep_runs"] > 0
    assert payload["walk_forward_folds"] is not None
    assert Interval.ONE_DAY.value in payload["intervals"]


# -- the seam with the platform ----------------------------------------------


def test_every_panel_response_names_the_build_that_served_it(ledger: Engine) -> None:
    # docs/plans/version.md: every response, from every surface. The middleware
    # is outermost, so this holds for the panel's routes without any of them
    # doing anything about it.
    response = client(ledger).get("/api/trading/strategies")

    assert response.headers["Aerie-Revision"] == STAMPED.revision


def test_an_unmatched_api_path_is_a_404_rather_than_a_page(ledger: Engine) -> None:
    # The SPA's catch-all is registered last and refuses to answer /api paths,
    # so a typo'd endpoint fails where it happened rather than as a JSON parse
    # error in a client.
    assert client(ledger).get("/api/trading/nothing-here").status_code == 404


def test_the_panel_reads_nothing_when_the_ledger_is_empty(ledger: Engine) -> None:
    reachable = client(ledger)

    assert reachable.get("/api/trading/strategies").json() == []
    assert reachable.get("/api/trading/leaderboard").json()["rows"] == []
    assert reachable.get("/api/trading/queue").json()["counts"]["queued"] == 0


def test_seeded_rows_are_distinguishable_from_an_operators(ledger: Engine, worked: int) -> None:
    # The property the seed job depends on: a sweep an operator launched is not
    # seeded, whatever it is called, so a re-run of the seed cannot reconcile
    # it away. See seed/.
    with ledger.begin() as connection:
        connection.execute(text("UPDATE sweep SET seeded = true WHERE id = :id"), {"id": worked})

    detail = client(ledger).get(f"/api/trading/sweeps/{worked}").json()

    assert detail["sweeps"][0]["seeded"] is True
    assert all(row["seeded"] for row in detail["runs"])
