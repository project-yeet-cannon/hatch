"""Everything the generator can be told, as values rather than as code.

docs/plans/trading.md Phase 2 asks for exactly this - "universe, seed, starting
price level, drift and volatility, all values rather than code" - and the
reason is provenance. The whole configuration is written to the ``data_source``
row when the provider registers (``providers/registry.py``), so a run is
reproducible from its record alone. A parameter that lives in a module instead
of in this model is one that does not appear in that record and therefore is
not part of the reproduction.

**Drift defaults to zero, and that is not a placeholder.** The generator is the
plan's null hypothesis: it has zero alpha by construction, so any strategy
posting an attractive Sharpe against it is provably overfit, which is what
turns Phase 6's gate from a judgment call into arithmetic. A non-zero drift
would give a buy-and-hold an edge to find, and the honesty layer would be
measuring against a moving target.
"""

from __future__ import annotations

from datetime import date

from pydantic import BaseModel, ConfigDict, Field, field_validator

__all__ = [
    "DEFAULT_UNIVERSE",
    "SYNTHETIC_GENERATOR_VERSION",
    "SyntheticConfig",
    "SyntheticSymbol",
]

#: Bumped whenever a change to the generator would produce different numbers
#: for the same coordinates - a different noise function, a different walk, a
#: different bar construction. It is recorded in provenance, so a result
#: produced before the change is distinguishable from one produced after it
#: rather than being quietly incomparable.
#:
#: This is a *contract* version, not a release version: adding a field to a
#: chain row does not bump it, changing what the existing fields contain does.
SYNTHETIC_GENERATOR_VERSION = 1


class SyntheticSymbol(BaseModel):
    """One instrument in the synthetic universe.

    ``annual_drift`` and ``annual_volatility`` are per-symbol overrides;
    unset means the configuration's defaults, which is the common case. They
    exist because Phase 5's sweeps and Phase 6's gate want more than one shape
    of series to look at - a low-volatility name and a high-volatility one
    exercise a cost model differently - and because that is a configuration
    change rather than a code change is the point of the phase.
    """

    model_config = ConfigDict(frozen=True, extra="forbid")

    symbol: str = Field(min_length=1, max_length=32)
    start_price: float = Field(gt=0, le=1_000_000)
    average_daily_volume: int = Field(default=1_000_000, gt=0)
    annual_drift: float | None = None
    annual_volatility: float | None = Field(default=None, gt=0)
    has_options: bool = True

    @field_validator("symbol")
    @classmethod
    def _upper(cls, value: str) -> str:
        return value.upper()


#: Nasdaq's own test tickers, and chosen for exactly the reason they exist.
#:
#: A synthetic universe named SPY and AAPL is a loaded gun: a screenshot of a
#: leaderboard, a row in a lake partition or a figure quoted out of context
#: reads as a claim about the real instrument, and nothing downstream can tell
#: the difference. ZVZZT and its siblings are reserved by the exchange for
#: precisely this purpose and cannot be mistaken for anything, which makes the
#: guardrail visible in the data itself rather than only in a ``data_source``
#: join.
#:
#: The starting prices and volatilities span an order of magnitude on purpose:
#: a strike ladder's increment depends on the price level, so a universe that
#: is all $100 names would leave three quarters of the ladder logic
#: unexercised.
DEFAULT_UNIVERSE: tuple[SyntheticSymbol, ...] = (
    SyntheticSymbol(symbol="ZVZZT", start_price=100.0, average_daily_volume=4_000_000),
    SyntheticSymbol(
        symbol="ZWZZT", start_price=18.5, average_daily_volume=1_200_000, annual_volatility=0.45
    ),
    SyntheticSymbol(
        symbol="ZXZZT", start_price=412.0, average_daily_volume=800_000, annual_volatility=0.12
    ),
    SyntheticSymbol(
        symbol="ZBZZT", start_price=47.25, average_daily_volume=2_500_000, annual_volatility=0.28
    ),
    # No options board. The plan's collectors take a watchlist for chains that
    # is not the whole universe, and a universe where every name has options is
    # one where "this symbol has no chain" is never exercised.
    SyntheticSymbol(
        symbol="ZJZZT", start_price=6.75, average_daily_volume=300_000, has_options=False
    ),
)


class SyntheticConfig(BaseModel):
    """The generator's whole configuration.

    Serialised verbatim into the ``data_source`` row, so every field here is
    part of the reproduction contract and none of them may hold a secret -
    there are none to hold, which is a property of a source that talks to
    nothing.
    """

    model_config = ConfigDict(frozen=True, extra="forbid")

    #: Every value the generator produces is a function of this and the
    #: coordinates. Changing it is changing to a different universe, not
    #: perturbing this one.
    seed: int = Field(default=20260902, ge=0)

    #: The exchange whose sessions and holidays the generator honours. Real,
    #: even though the prices are not - a synthetic session that ran 24/7 would
    #: leave Phase 3's calendar handling completely unexercised, and calendar
    #: handling is the one piece of that phase that fails silently rather than
    #: loudly.
    calendar: str = "XNYS"

    #: Session zero. Every price is derived by walking sessions from here, so
    #: moving it moves every series - it is part of the reproduction contract
    #: for the same reason the seed is. Far enough back to give a backtest
    #: room, near enough that the walk is a few thousand steps rather than a
    #: few hundred thousand.
    anchor: date = date(2015, 1, 2)

    #: Annualised, and zero by default. See the module docstring.
    annual_drift: float = 0.0
    annual_volatility: float = Field(default=0.22, gt=0)

    #: Sessions per year, the divisor that turns the annual figures above into
    #: per-session ones. A convention rather than a measurement - the actual
    #: count varies by a day or two a year, and the generator is noise.
    sessions_per_year: int = Field(default=252, gt=0)

    universe: tuple[SyntheticSymbol, ...] = DEFAULT_UNIVERSE

    # -- Option board -------------------------------------------------------
    #: How many weekly expiries and how many monthlies the board carries. Small
    #: by default: the collector's job is proven by a board with the right
    #: shape, and Phase 3 measures lake growth at the row counts a *real* chain
    #: implies rather than at this one's.
    weekly_expiries: int = Field(default=4, ge=0, le=52)
    monthly_expiries: int = Field(default=3, ge=0, le=24)

    #: Strikes each side of at-the-money. The ladder is symmetric, so the board
    #: carries ``2 * strikes_per_side + 1`` strikes per expiry per right.
    strikes_per_side: int = Field(default=8, ge=1, le=100)

    @field_validator("universe")
    @classmethod
    def _no_duplicates(cls, value: tuple[SyntheticSymbol, ...]) -> tuple[SyntheticSymbol, ...]:
        if not value:
            raise ValueError("the synthetic universe cannot be empty")
        symbols = [entry.symbol for entry in value]
        duplicates = sorted({symbol for symbol in symbols if symbols.count(symbol) > 1})
        if duplicates:
            raise ValueError(
                f"duplicate symbols in the synthetic universe: {', '.join(duplicates)}"
            )
        return value

    def symbol(self, symbol: str) -> SyntheticSymbol:
        """The configuration for one symbol, or ``KeyError``.

        A ``KeyError`` rather than a generated default: a request for a symbol
        that is not in the universe is a typo or a stale watchlist, and
        inventing a series for it would answer the question instead of
        reporting it. That is the same failure Phase 3 calls "recording silence
        as data", one layer up.
        """
        for entry in self.universe:
            if entry.symbol == symbol.upper():
                return entry
        raise KeyError(f"{symbol!r} is not in the synthetic universe")

    @property
    def symbols(self) -> tuple[str, ...]:
        return tuple(entry.symbol for entry in self.universe)

    def drift_for(self, entry: SyntheticSymbol) -> float:
        return entry.annual_drift if entry.annual_drift is not None else self.annual_drift

    def volatility_for(self, entry: SyntheticSymbol) -> float:
        return (
            entry.annual_volatility
            if entry.annual_volatility is not None
            else self.annual_volatility
        )
