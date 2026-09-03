"""What the honesty layer costs and how hard it looks, as values.

Reachable as ``TRADING_HONESTY__<field>`` - and, for the walk-forward block,
``TRADING_HONESTY__WALK_FORWARD__FOLDS`` - or replaceable wholesale with JSON
in ``TRADING_HONESTY``, which is the shape every other config block on
``Settings`` already has.

**``WalkForwardSpec`` is defined here and stored on the sweep**, which is the
one thing about this file worth understanding before changing it. The numbers a
walk-forward ran with are part of what its result means: a five-fold figure and
a twenty-fold figure over the same window are different measurements, and a
leaderboard that sorted them together while reading the fold count out of the
*current* environment would relabel every historical result the day an operator
changed a setting. So the environment supplies the default a launcher bakes
into ``sweep.spec``, and the run is thereafter described by the spec. That is
the same split ``providers/registry.py`` makes for the generator's
configuration, for the same reason.

Two of the remaining knobs are worth understanding.

``cost_stress_multiple``
    every result is re-scored with the cost model's rates multiplied by this,
    and the difference is reported. It costs a second backtest per run, which
    is to say it roughly doubles the compute a sweep uses - accepted knowingly,
    because *"a strategy that only works at zero slippage is identified as such
    automatically"* is the plan's own wording and an opt-in check is one nobody
    opts into. Set it to 1 to switch the re-score off; the metrics then go
    absent rather than reporting a comparison against itself.

``max_fold_evaluations``
    the ceiling on one walk-forward. A walk-forward over a grid of N points and
    F folds is N x F backtests inside a single work item, and a mistyped step
    turns that from ninety-five into half a million without changing the
    command that launched it. The run fails with the arithmetic in its error
    rather than holding a lease for an afternoon - the same argument
    ``max_sweep_runs`` makes one layer up, applied where the cost is incurred
    rather than where it is requested.
"""

from __future__ import annotations

from decimal import Decimal

from pydantic import BaseModel, ConfigDict, Field

__all__ = ["HonestyConfig", "WalkForwardSpec"]


class WalkForwardSpec(BaseModel):
    """How a sweep is evaluated out of sample. Stored on the sweep's ``spec``.

    ``frozen`` and ``extra="forbid"`` like every other spec in the silo: this
    round-trips through JSONB and is read back by a worker in another process,
    and a field that was silently dropped on the way in would be a run that
    quietly used a different number of folds from the one that was asked for.
    """

    model_config = ConfigDict(frozen=True, extra="forbid")

    #: Rolling train/test splits. Five is a compromise with a shape rather than
    #: a round number: more folds means each test window is shorter, and a
    #: Sharpe over a two-month window is mostly noise about the Sharpe; fewer
    #: means the parameters are re-chosen so rarely that the exercise stops
    #: being a walk-forward and becomes one holdout.
    folds: int = Field(default=5, ge=2, le=50)

    #: How many test windows fit in one train window. Three means a parameter
    #: set is chosen on three times as much history as it is then judged over,
    #: which keeps a fold's selection from being as noisy as the thing it is
    #: selecting against.
    train_multiple: int = Field(default=3, ge=1, le=20)

    #: Which metric a fold's parameters are chosen on. A name from
    #: ``engine/metrics.py``, higher-is-better. Sharpe rather than total
    #: return, because selecting on return alone picks the fold's luckiest path
    #: every time.
    objective: str = Field(default="sharpe", min_length=1, max_length=48)


class HonestyConfig(BaseModel):
    """Everything Phase 6 reads from the environment."""

    model_config = ConfigDict(frozen=True, extra="forbid")

    #: The default a launcher bakes into every new sweep. See the module
    #: docstring on why the sweep carries it rather than reading it back.
    walk_forward: WalkForwardSpec = WalkForwardSpec()

    #: The cost model's rates, multiplied by this, for the re-score. 1 disables.
    cost_stress_multiple: Decimal = Field(default=Decimal(5), ge=1, le=1_000)

    #: The universe the broad-index baseline buys and holds. Empty means "every
    #: symbol this installation collects bars for", resolved at the worker's
    #: composition root - which is the broadest thing a lake with no index in
    #: it actually has, and is a comparison rather than a placeholder. A run
    #: whose own universe is already that whole set will see its two baselines
    #: agree, which is honest and visible rather than a second number invented
    #: to look like an independent check.
    index_symbols: tuple[str, ...] = ()

    #: The ceiling on one walk-forward, in backtests. See the module docstring.
    max_fold_evaluations: int = Field(default=20_000, ge=1, le=10_000_000)
