"""How a run was priced, in a form a database can hold and a worker can rebuild.

``engine/broker.Costs`` carries two model *objects* and a ``describe()`` that
renders them for a human. Neither half is enough on its own for a queue: the
objects do not serialise, and the prose does not reconstruct. A worker pulls a
row and has to build the identical cost models the launcher intended, or the
run it produces is not the run that was asked for - and the failure is silent,
because a backtest priced with the wrong slippage still finishes and still
looks plausible.

So the ``run.costs`` column holds this model's fields *and* the two description
strings side by side in one JSONB blob. The fields are what
``CostSpec.from_blob`` reads back; the descriptions are what a person reading
the row in ``psql`` needs, and they are written by the same code that builds the
models rather than typed twice. ``extra="ignore"`` is what lets one blob do both
jobs - and is the one place in this silo that deviates from ``Params``' strict
``extra="forbid"``, deliberately, because here the extra keys are ours.

**Phase 6 varies exactly one field of this.** Its transaction-cost sensitivity
bullet re-scores every result at a higher cost assumption, which is this model
with a larger ``slippage_bps`` and everything else equal - which is why the cost
model is a value on the run rather than a global the engine reads.
"""

from __future__ import annotations

from collections.abc import Mapping
from decimal import Decimal

from pydantic import BaseModel, ConfigDict, Field

from aerie_trading.engine.broker import (
    BpsSlippage,
    Costs,
    NoCommission,
    NoSlippage,
    PerUnitCommission,
)

__all__ = ["RETAIL_EQUITY", "CostSpec"]


class CostSpec(BaseModel):
    """A slippage rate and a commission schedule, as four numbers.

    Narrower than the ``SlippageModel`` and ``CommissionModel`` protocols allow,
    and that is the trade being made: a spec that could name an arbitrary class
    would let a ``run`` row ask a worker to import something this build does not
    ship, which is a deploy skew that surfaces as a failed sweep. The two models
    the engine ships are expressible here; a third arrives by widening this
    model, which is a migration of nothing because the column is JSONB.
    """

    model_config = ConfigDict(frozen=True, extra="ignore")

    #: Basis points paid against the fill, each way. Zero selects
    #: ``NoSlippage`` rather than a zero-rate ``BpsSlippage``, so that a free
    #: run says so in its own description.
    slippage_bps: Decimal = Field(default=Decimal("2"), ge=0, le=10_000)
    commission_per_unit: Decimal = Field(default=Decimal("0"), ge=0)
    commission_minimum: Decimal = Field(default=Decimal("0"), ge=0)
    commission_maximum: Decimal | None = Field(default=None, ge=0)

    def build(self) -> Costs:
        """The engine objects this describes."""
        slippage = NoSlippage() if self.slippage_bps == 0 else BpsSlippage(self.slippage_bps)
        charges_nothing = (
            self.commission_per_unit == 0
            and self.commission_minimum == 0
            and self.commission_maximum is None
        )
        commission = (
            NoCommission()
            if charges_nothing
            else PerUnitCommission(
                per_unit=self.commission_per_unit,
                minimum=self.commission_minimum,
                maximum=self.commission_maximum,
            )
        )
        return Costs(slippage=slippage, commission=commission)

    def to_blob(self) -> dict[str, object]:
        """What goes into ``run.costs``: the fields, plus the prose.

        Both, in one flat object, for the reason the module docstring gives.
        The descriptions come from ``build()`` rather than from a format string
        here, so the row cannot describe a cost model different from the one it
        reconstructs.
        """
        blob: dict[str, object] = dict(self.model_dump(mode="json"))
        blob.update(self.build().describe())
        return blob

    @classmethod
    def from_blob(cls, blob: Mapping[str, object]) -> CostSpec:
        """Read a spec back out of a ``run.costs`` column."""
        return cls.model_validate(dict(blob))


#: The default, matching ``engine/broker.RETAIL_EQUITY_COSTS`` value for value.
#: Named here as well because a sweep launched without a cost model must be
#: priced, not free - a backtester whose default costs are zero is one whose
#: default answer is optimistic.
RETAIL_EQUITY = CostSpec()
