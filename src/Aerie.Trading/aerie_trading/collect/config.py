"""What to collect, how often, and how far back - as values rather than code.

The plan's rule for the synthetic generator applies here for the same reason:
*"Start with a small watchlist and a conservative interval; both are
configuration, and widening them later costs nothing while starting late costs
everything."* A watchlist that lives in a module is one that needs a rebuild and
a deploy to widen, which is exactly the friction that turns "we should start
collecting SPY chains" into next week's job.

Every field here is reachable as ``TRADING_COLLECTION__<field>``, or the whole
model can be replaced at once with JSON in ``TRADING_COLLECTION`` - the same
shape ``SyntheticConfig`` has on ``Settings``, so an operator learns the
convention once.

**The defaults describe the synthetic universe, and that is not a coupling to
it.** They are the values that make a fresh clone collect something rather than
nothing, drawn from the provider that a fresh clone has. Phase 8 changes the
value of ``bar_symbols`` and ``chain_watchlist``; it changes nothing in this
file.
"""

from __future__ import annotations

from pydantic import BaseModel, ConfigDict, Field, field_validator

from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.config import DEFAULT_UNIVERSE

__all__ = ["CollectionConfig"]


def _default_bar_symbols() -> tuple[str, ...]:
    return tuple(entry.symbol for entry in DEFAULT_UNIVERSE)


def _default_chain_watchlist() -> tuple[str, ...]:
    # Only the names that have a board. A default watchlist naming a symbol
    # with no options would make the collector's "this symbol has no chain"
    # path fire on every run of a correctly configured installation, which is
    # how a warning stops being read. The path is exercised by a test that
    # configures it deliberately instead.
    return tuple(entry.symbol for entry in DEFAULT_UNIVERSE if entry.has_options)


class CollectionConfig(BaseModel):
    """The collectors' whole configuration."""

    model_config = ConfigDict(frozen=True, extra="forbid")

    #: Which equities get bars. Everything in the universe by default: bars are
    #: the cheap dataset - a session of minute bars for one symbol is 390 rows -
    #: and the expensive one is chains, which has its own narrower list below.
    bar_symbols: tuple[str, ...] = Field(default_factory=_default_bar_symbols)

    #: Which bar sizes to collect. Daily and minute, because those are the two
    #: a strategy actually asks for: the intermediate intervals are derivable
    #: from minute bars by resampling, and storing them as well would be four
    #: more partitions per symbol per month carrying no information the lake
    #: does not already hold.
    bar_intervals: tuple[Interval, ...] = (Interval.ONE_DAY, Interval.ONE_MINUTE)

    #: How many sessions back a backfill goes when it is not given a start
    #: date. A year of sessions - far enough for a backtest to mean something,
    #: short enough that the first run of a new installation finishes while
    #: someone is still watching it.
    backfill_sessions: int = Field(default=252, gt=0, le=10_000)

    #: Whose boards get snapshotted. Deliberately the same size as the universe
    #: today and deliberately a separate field: the moment this list is a real
    #: watchlist it will be a fraction of the symbols that have bars.
    chain_watchlist: tuple[str, ...] = Field(default_factory=_default_chain_watchlist)

    #: How often through a session a board is snapshotted, in minutes. Thirty
    #: is the conservative interval the plan asks to start at: thirteen
    #: snapshots a session, which is enough to see a board move and few enough
    #: that widening it later is a decision made against a measured file size
    #: rather than against a guess.
    #:
    #: It must divide 390 - a regular session - so that snapshots land on the
    #: same offsets every day and a missed one is visible as a hole in a
    #: regular series rather than as a slightly different rhythm.
    chain_snapshot_minutes: int = Field(default=30, ge=1, le=390)

    #: Whether to snapshot the board at the closing bell in addition to the
    #: interval above. The close is the observation every options analysis
    #: starts from and it is the one an interval that divides the session
    #: evenly always misses - the last regular snapshot lands one interval
    #: before it, because the session is half-open.
    chain_snapshot_at_close: bool = True

    @field_validator("bar_symbols", "chain_watchlist")
    @classmethod
    def _upper_and_unique(cls, value: tuple[str, ...]) -> tuple[str, ...]:
        symbols = tuple(dict.fromkeys(entry.strip().upper() for entry in value))
        if any(not symbol for symbol in symbols):
            raise ValueError("a watchlist cannot contain an empty symbol")
        return symbols

    @field_validator("bar_intervals")
    @classmethod
    def _at_least_one_interval(cls, value: tuple[Interval, ...]) -> tuple[Interval, ...]:
        intervals = tuple(dict.fromkeys(value))
        if not intervals:
            raise ValueError("bar_intervals cannot be empty")
        return intervals
