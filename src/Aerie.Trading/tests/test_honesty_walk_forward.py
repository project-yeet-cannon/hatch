"""Phase 6's gate, and the walk-forward machinery it runs on.

docs/plans/trading.md Phase 6: *"**Gate:** a deliberately overfit strategy -
parameters fit to noise - ranks poorly on the leaderboard, and its in-sample
and walk-forward numbers visibly diverge. If it ranks well, this phase is not
done."* And on why that gate can be exact here:

    **The synthetic source makes this gate exact rather than impressionistic.**
    Phase 2's generator has zero alpha by construction and asserts it
    (``tests/test_synthetic_zero_alpha.py``), so *every* result over synthetic
    data is a false positive by definition, and the best of a large sweep over
    it is the strongest false positive the machinery can manufacture. Sweep it
    deliberately, take the winner, and require that the walk-forward number and
    the selection-adjusted figure both collapse toward nothing.

**The overfit strategy is not a fixture, it is the shipped one.** The plan's
own construction is a wide grid over data with no edge in it; ``ma_crossover``
swept over sixty combinations of two windows is exactly *"parameters fit to
noise"*, and building a bespoke memorising strategy for the gate would be
measuring a thing this silo does not ship.

**The false positive here is a relative one, and that was measured rather than
assumed.** The first version of this file asserted that the sweep's winner
posts a *positive* Sharpe, on the reasoning that maximising over noise
manufactures one. It does not, over this universe: all fifty-seven surviving
crossovers score between -0.90 and -0.18, because a trend follower on a
driftless walk is out of the market half the time and pays for every whipsaw,
and no amount of parameter search rescues that. The selection effect is
nonetheless right there and is the whole of what the honesty layer has to
catch: the *best* of the sweep beats the *median* of the sweep by about 0.39 of
a Sharpe, and every point of that gap is selection rather than skill.

So the gate is written against the gap rather than against the sign, which is
the stronger form of the same claim - a machinery that only noticed inflated
figures when they were above zero would be blind to exactly the case a
leaderboard sorted within one strategy presents. Three assertions carry it: the
winner beats the typical member of its own sweep, the walk-forward figure does
not inherit that gap and lands in the bottom half of the same distribution -
*"ranks poorly on the leaderboard"*, the plan's own words - and the
selection-adjusted figure is at or below zero.

**What deliberately is not asserted:** the size of any single draw. That is the
mistake Phase 4 retired when it stopped claiming which strategy wins. Every
assertion below is a relation between numbers computed from the *same* draw,
which is a property of maximising over noise rather than of this noise.
"""

from collections.abc import Generator
from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from itertools import pairwise
from pathlib import Path

import pytest

from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.history import BarHistory, load_history
from aerie_trading.engine.metrics import SHARPE, TOTAL_RETURN, compute_metrics, periods_per_year
from aerie_trading.honesty.selection import haircut_sharpe
from aerie_trading.honesty.walkforward import WalkForwardTooLarge, walk_forward
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.runs.sweep import SweepSpec, plan_sweep
from aerie_trading.strategies import spec_for

SYMBOLS = ("ZVZZT", "ZWZZT", "ZXZZT")
FIRST_SESSION = date(2021, 1, 4)
LAST_SESSION = date(2025, 12, 31)
WINDOW = (datetime(2021, 1, 1, tzinfo=UTC), datetime(2026, 1, 1, tzinfo=UTC))

#: Deliberately wide. Sixty combinations before pruning, over data with no edge
#: in it, is the strongest false positive this machinery can manufacture at a
#: cost a test suite can pay - and the point of the gate is that the machinery
#: says so rather than that the sweep is enormous.
GRID = {
    "fast": tuple(Decimal(value) for value in (5, 10, 15, 20, 25, 30)),
    "slow": tuple(Decimal(value) for value in (20, 40, 60, 80, 100, 120, 140, 160, 180, 200)),
}


@pytest.fixture(scope="module")
def history(tmp_path_factory: pytest.TempPathFactory) -> Generator[BarHistory, None, None]:
    """Five years of daily bars, through the lake rather than built by hand.

    Through the lake because the gate is a claim about the pipeline the product
    actually runs, and because Phase 2's zero-alpha assertion is about what the
    provider generates - reading it back the way a worker does is what makes
    this the same data that test measured.
    """
    root: Path = tmp_path_factory.mktemp("honesty-lake")
    provider = SyntheticMarketDataProvider()
    writer = LakeWriter(
        root=root,
        provenance=Provenance("synthetic", "c" * 40, datetime(2026, 1, 1, tzinfo=UTC)),
    )
    sessions = provider.market_hours(FIRST_SESSION, LAST_SESSION)
    writer.write_bars(
        provider.bars(
            SYMBOLS, Interval.ONE_DAY, sessions[0].open, sessions[-1].close + timedelta(minutes=1)
        )
    )
    with LakeReader(root) as reader:
        yield load_history(reader, SYMBOLS, Interval.ONE_DAY, *WINDOW)


@pytest.fixture(scope="module")
def param_sets(history: BarHistory) -> tuple[dict[str, object], ...]:
    plan = plan_sweep(
        SweepSpec(
            name="overfit",
            strategy="ma_crossover",
            grid=GRID,
            symbols=SYMBOLS,
            window_start=WINDOW[0],
            window_end=WINDOW[1],
        )
    )
    return tuple(dict(entry) for entry in plan.param_sets)


# -- the machinery ----------------------------------------------------------


def test_a_fold_carries_the_account_it_was_handed(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    evaluation = walk_forward(
        history,
        spec_for("ma_crossover"),
        param_sets[:4],
        window=WINDOW,
        folds=3,
        starting_cash=Decimal(100_000),
    )

    assert len(evaluation.outcomes) == 3
    assert evaluation.outcomes[0].starting_cash == Decimal("100000.00")
    for earlier, later in pairwise(evaluation.outcomes):
        # One account, handed on. The continuity is what makes the stitched
        # curve a real equity series rather than a compounding convention.
        assert later.starting_cash == earlier.ending_equity
    assert evaluation.result.final_equity == evaluation.outcomes[-1].ending_equity


def test_the_stitched_result_covers_only_the_test_windows(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    evaluation = walk_forward(
        history, spec_for("ma_crossover"), param_sets[:4], window=WINDOW, folds=3
    )

    assert evaluation.out_of_sample.start == evaluation.outcomes[0].fold.test_start
    assert evaluation.out_of_sample.end == evaluation.outcomes[-1].fold.test_end
    # Nothing before the first test window is in the curve, which is the whole
    # of "results stitched from the test windows only".
    assert evaluation.result.curve[0].timestamp >= evaluation.out_of_sample.start
    assert evaluation.result.bars < len(history)


def test_the_same_evaluation_twice_produces_the_same_fingerprint(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    # Including the tie-break, which is the part that would otherwise be a
    # function of dictionary iteration order.
    first = walk_forward(history, spec_for("ma_crossover"), param_sets, window=WINDOW, folds=3)
    second = walk_forward(history, spec_for("ma_crossover"), param_sets, window=WINDOW, folds=3)

    assert first.result.fingerprint() == second.result.fingerprint()
    assert [outcome.params for outcome in first.outcomes] == [
        outcome.params for outcome in second.outcomes
    ]


def test_an_absurd_evaluation_is_refused_before_it_runs(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    with pytest.raises(WalkForwardTooLarge, match="Narrow the grid"):
        walk_forward(
            history, spec_for("ma_crossover"), param_sets, window=WINDOW, folds=5, ceiling=10
        )


# -- the gate ----------------------------------------------------------------


def test_the_best_of_a_sweep_over_noise_does_not_survive_the_honesty_layer(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    strategy = spec_for("ma_crossover")

    in_sample = sorted(
        metrics[SHARPE]
        for metrics in (
            compute_metrics(run_backtest(strategy.build(params), history, name="ma_crossover"))
            for params in param_sets
        )
        if SHARPE in metrics
    )
    best = in_sample[-1]
    median = in_sample[len(in_sample) // 2]

    # The selection effect, before anything corrects for it. This gap is what a
    # leaderboard sorted on the raw figure puts at the top of the page, and
    # every point of it was bought by looking at fifty-seven parameter sets
    # over data that has nothing in it to find.
    assert best - median > Decimal("0.2")

    # 1. Selection accounting collapses it. The haircut is what the best of
    #    this many trials earns by luck alone over this many bars, and what is
    #    left is what a real edge would have been - which here is nothing.
    observations = len(history) - 1
    adjusted = haircut_sharpe(
        best, len(param_sets), observations, periods_per_year(Interval.ONE_DAY)
    )
    assert adjusted <= 0

    # 2. The walk-forward collapses it by a different route: parameters chosen
    #    on each train window and judged only on the window after it. It does
    #    not inherit the winner's advantage...
    evaluation = walk_forward(
        history, strategy, param_sets, window=WINDOW, folds=5, starting_cash=Decimal(100_000)
    )
    forward = compute_metrics(evaluation.result)[SHARPE]

    assert best - forward > Decimal("0.3")

    # 3. ...and it ranks poorly against the very distribution it was selected
    #    out of, which is the gate's own wording. Below the median of the sweep
    #    it walked, not merely below the winner.
    assert forward < median


def test_the_walk_forward_winner_changes_between_folds(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    # The other face of the same finding, and the one that reads as a diagnosis
    # rather than as a number: parameters fitted to noise do not survive to the
    # next window, so the fold winners disagree with each other. A grid with a
    # real edge in it would keep choosing the same corner.
    evaluation = walk_forward(history, spec_for("ma_crossover"), param_sets, window=WINDOW, folds=5)

    chosen = {tuple(sorted(outcome.params.items())) for outcome in evaluation.outcomes}
    assert len(chosen) > 1


def test_every_fold_scored_the_whole_grid(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    # A fold that silently scored three of sixty candidates would produce a
    # winner meaning far less than the sweep's trial count claims, and the
    # count is on the fold row precisely so that is visible rather than
    # assumed. Over five years of daily bars every candidate has room to warm
    # up, so here it should be all of them.
    evaluation = walk_forward(history, spec_for("ma_crossover"), param_sets, window=WINDOW, folds=5)

    assert {outcome.candidates for outcome in evaluation.outcomes} == {len(param_sets)}


def test_a_walk_forward_reports_a_total_return_over_its_own_starting_cash(
    history: BarHistory, param_sets: tuple[dict[str, object], ...]
) -> None:
    evaluation = walk_forward(
        history,
        spec_for("ma_crossover"),
        param_sets[:4],
        window=WINDOW,
        folds=3,
        starting_cash=Decimal(100_000),
    )
    metrics = compute_metrics(evaluation.result)

    assert metrics[TOTAL_RETURN] == pytest.approx(
        Decimal(evaluation.result.final_equity) / Decimal(100_000) - 1, abs=Decimal("0.000001")
    )
