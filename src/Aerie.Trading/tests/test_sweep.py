"""Expanding a grid, pruning it, and refusing to enqueue an unconfirmed one.

Nothing here touches a database. The plan's *"sweep size is estimated and
confirmed before enqueueing"* is a property of ``plan_sweep`` and
``enqueue_sweep``'s signature rather than of anything they write, which is what
makes it assertable without a Postgres - and is also the argument for the
handshake being a required argument rather than a printed number.
"""

from datetime import UTC, datetime
from decimal import Decimal
from typing import cast

import pytest
from sqlalchemy.orm import Session

from aerie_trading.providers.base import Interval
from aerie_trading.runs.costs import CostSpec
from aerie_trading.runs.demo import DEMO_SWEEPS, DEMO_SYMBOLS, DEMO_WINDOW, demo_sweeps
from aerie_trading.runs.sweep import (
    SweepNotConfirmed,
    SweepSpec,
    SweepTooLarge,
    enqueue_sweep,
    full_grid,
    plan_sweep,
)
from aerie_trading.strategies import spec_for

WINDOW = (datetime(2024, 1, 1, tzinfo=UTC), datetime(2025, 1, 1, tzinfo=UTC))


def spec(**overrides: object) -> SweepSpec:
    values: dict[str, object] = {
        "name": "test",
        "strategy": "ma_crossover",
        "grid": {"fast": (Decimal(5), Decimal(10)), "slow": (Decimal(20), Decimal(40))},
        "symbols": ("ZVZZT",),
        "interval": Interval.ONE_DAY,
        "window_start": WINDOW[0],
        "window_end": WINDOW[1],
    }
    values.update(overrides)
    return SweepSpec.model_validate(values)


# -- expansion --------------------------------------------------------------


def test_a_grid_expands_to_its_cross_product() -> None:
    plan = plan_sweep(spec())

    assert plan.total == 4
    assert plan.combinations == 4
    assert plan.rejected == 0
    assert [(entry["fast"], entry["slow"]) for entry in plan.param_sets] == [
        (5, 20),
        (5, 40),
        (10, 20),
        (10, 40),
    ]


def test_expansion_is_deterministic_regardless_of_how_the_grid_was_spelled() -> None:
    # Dimensions are walked in sorted key order, so a grid built by a UI in one
    # order and by a CLI in another enqueues the same runs in the same
    # sequence. A sweep whose partial results depend on dictionary iteration
    # order is one where two attempts at the same search disagree.
    forwards = plan_sweep(spec(grid={"fast": (Decimal(5),), "slow": (Decimal(20), Decimal(40))}))
    backwards = plan_sweep(spec(grid={"slow": (Decimal(20), Decimal(40)), "fast": (Decimal(5),)}))

    assert list(forwards.param_sets) == list(backwards.param_sets)


def test_a_strategy_with_nothing_to_sweep_expands_to_exactly_one_run() -> None:
    # The empty cross product, which itertools.product answers with one empty
    # tuple. buy_and_hold has no swept parameters at all, so its "sweep" is a
    # single run at every default - which is how the demo's baseline is
    # expressed without a second code path.
    plan = plan_sweep(spec(strategy="buy_and_hold", grid={}))

    assert plan.total == 1
    assert plan.param_sets == ({},)


def test_the_declared_range_is_the_one_that_is_swept() -> None:
    # full_grid reads the range off the field rather than taking one. The
    # numbers below are ma_crossover's own declarations - fast 2..50 step 1,
    # slow 5..200 step 5 - and if that file changes, this changes with it,
    # which is the property a launcher-side range would not have.
    grid = full_grid(spec_for("ma_crossover"), ["fast", "slow"])

    assert len(grid["fast"]) == 49
    assert grid["fast"][0] == 2
    assert grid["fast"][-1] == 50
    assert len(grid["slow"]) == 40
    assert grid["slow"][0] == 5
    assert grid["slow"][-1] == 200


def test_sweeping_a_parameter_with_no_declared_range_is_refused() -> None:
    with pytest.raises(LookupError, match="declares no sweep range"):
        full_grid(spec_for("buy_and_hold"), ["nonexistent"])


def test_a_grid_naming_a_parameter_the_strategy_does_not_have_is_refused() -> None:
    with pytest.raises(LookupError, match="has no parameter"):
        plan_sweep(spec(grid={"windows": (Decimal(5),)}))


# -- pruning ----------------------------------------------------------------


def test_the_strategys_own_validator_prunes_the_invalid_corner() -> None:
    # ma_crossover requires fast < slow, in its model rather than in on_bar,
    # so the corner is rejected while the param_set is being built instead of
    # after a worker spent a minute producing it. Four combinations, and
    # (10, 10) is not one a run can be made of.
    plan = plan_sweep(
        spec(grid={"fast": (Decimal(5), Decimal(10)), "slow": (Decimal(10), Decimal(20))})
    )

    assert plan.combinations == 4
    assert plan.total == 3
    assert plan.rejected == 1
    # Reported rather than swallowed: the difference between "the grid was
    # wrong" and "one corner of it was" is the whole content of this line.
    assert "must be below" in plan.rejection
    assert "3 run(s)" in plan.describe()
    assert "1 pruned" in plan.describe()


def test_a_grid_that_prunes_to_nothing_is_refused_rather_than_enqueued_empty() -> None:
    plan = plan_sweep(spec(grid={"fast": (Decimal(20),), "slow": (Decimal(20),)}))

    assert plan.total == 0
    with pytest.raises(ValueError, match="expands to no runs"):
        enqueue_sweep(_no_session(), plan, confirm=0, data_source_id=1)


def test_a_dimension_with_no_values_is_refused_at_the_spec() -> None:
    # Distinct from the case above: an empty axis makes the cross product empty
    # for a reason that is a typo rather than a constraint, and it should read
    # as one.
    with pytest.raises(ValueError, match="no values to sweep"):
        spec(grid={"fast": ()})


# -- the handshake ----------------------------------------------------------


def test_enqueueing_without_the_planned_count_is_refused() -> None:
    plan = plan_sweep(spec())

    with pytest.raises(SweepNotConfirmed) as refusal:
        enqueue_sweep(_no_session(), plan, confirm=99, data_source_id=1)

    # The refusal carries the true number, so a caller can print the right one
    # rather than sending the operator back to re-run the estimate.
    assert refusal.value.planned == 4
    assert refusal.value.confirmed == 99


def test_a_plan_above_the_ceiling_is_refused_even_when_confirmed() -> None:
    # The half of the guard that does not depend on the operator reading the
    # estimate. A mistyped step turns a 200-run grid into a 200,000-run one,
    # and the two commands look identical.
    plan = plan_sweep(spec())

    with pytest.raises(SweepTooLarge) as refusal:
        enqueue_sweep(_no_session(), plan, confirm=4, data_source_id=1, ceiling=2)

    assert refusal.value.planned == 4
    assert refusal.value.ceiling == 2


# -- the spec itself --------------------------------------------------------


def test_a_window_that_ends_before_it_starts_is_refused() -> None:
    with pytest.raises(ValueError, match="must end after it starts"):
        spec(window_start=WINDOW[1], window_end=WINDOW[0])


def test_a_naive_window_is_refused() -> None:
    with pytest.raises(ValueError, match="timezone-aware"):
        spec(window_start=datetime(2024, 1, 1), window_end=datetime(2025, 1, 1))


def test_symbols_are_uppercased_and_deduplicated() -> None:
    assert spec(symbols=("zvzzt", "ZVZZT", "zwzzt")).symbols == ("ZVZZT", "ZWZZT")


def test_a_spec_survives_the_json_round_trip_it_is_stored_as() -> None:
    # `sweep.spec` is JSONB and a control panel reads it back, so the model
    # dump has to validate as itself. A Decimal that dumped as a float would
    # come back as one and change the grid.
    original = spec(starting_cash=Decimal("50000.50"), costs=CostSpec(slippage_bps=Decimal("7")))

    assert SweepSpec.model_validate(original.model_dump(mode="json")) == original


# -- the demo ---------------------------------------------------------------


def test_the_demo_sweep_is_sized_the_way_its_module_says() -> None:
    grid, baseline = (plan_sweep(entry) for entry in DEMO_SWEEPS)

    # Twenty combinations, of which fast = slow = 20 is pruned by the
    # strategy's own validator, plus the one baseline run.
    assert grid.combinations == 20
    assert grid.rejected == 1
    assert grid.total == 19
    assert baseline.total == 1
    # Small enough to finish on a cold cluster in minutes, which is the plan's
    # sizing requirement stated as a number rather than as an intention.
    assert grid.total + baseline.total < 50


def test_the_demo_baseline_covers_the_identical_window_and_universe() -> None:
    # Phase 6 requires baselines "computed over the identical window", and a
    # baseline over a different one is not a baseline. This is the *only*
    # thing the demo claims - see runs/demo.py on why it deliberately asserts
    # nothing about who wins - so it is the assertion that has to hold.
    grid, baseline = DEMO_SWEEPS
    assert baseline.symbols == grid.symbols == DEMO_SYMBOLS
    assert len(DEMO_SYMBOLS) > 1
    assert (baseline.window_start, baseline.window_end) == (grid.window_start, grid.window_end)
    assert baseline.interval == grid.interval
    assert baseline.starting_cash == grid.starting_cash
    assert baseline.costs == grid.costs
    # And the window is Phase 4's, so the seeded leaderboard is that phase's
    # assertion made visible - see runs/demo.py.
    assert (grid.window_start, grid.window_end) == DEMO_WINDOW


def test_repointing_the_demo_moves_the_grid_and_its_baseline_together() -> None:
    # An installation that replaced its universe seeds the demo over what it
    # collects. Both specs or neither: a baseline over a different universe
    # from the grid beside it is not a baseline.
    grid, baseline = demo_sweeps(("ZWZZT", "ZXZZT"))

    assert grid.symbols == baseline.symbols == ("ZWZZT", "ZXZZT")
    assert (grid.window_start, grid.window_end) == DEMO_WINDOW
    assert plan_sweep(grid).total == 19


def test_the_demo_baseline_runs_before_the_grid_it_is_a_baseline_for() -> None:
    grid, baseline = DEMO_SWEEPS
    # Lowest first. The comparison should exist from the first crossover result
    # rather than after the last one.
    assert baseline.priority < grid.priority


class _Explodes:
    """A stand-in for the session these refusals never reach.

    Every refusal above raises before ``enqueue_sweep`` touches its session
    argument, which is itself the assertion: a refused sweep must not have
    written a ``strategy`` row on its way to being refused. Any attribute
    access at all fails the test that reached it.
    """

    def __getattr__(self, name: str) -> object:
        raise AssertionError(f"a refused sweep touched the session ({name})")


def _no_session() -> Session:
    # The cast is the whole point of the class above: it is deliberately not a
    # Session, and telling the checker it is one is how a test asserts that
    # nothing calls it.
    return cast(Session, _Explodes())
