"""The ``MarketDataProvider`` interface, and the records it hands back.

**Written before its second implementation exists, on purpose.**
docs/plans/trading.md Phase 2 argues the case at length: an interface drafted
alongside Schwab alone would quietly encode Schwab's quirks as though they were
the shape of market data. Drafted against the trivial implementation first, it
has to be honest, and the vendor gets bent to the interface rather than the
reverse.

Four calls, which is the whole surface:

===================  =========================================================
``market_hours()``   which days are sessions, and when they open and close.
``bars()``           OHLCV over a window, at an interval.
``quotes()``         the top of book at an instant.
``chain()``          every option contract on an underlying at an instant.
===================  =========================================================

Two conventions that are load-bearing rather than stylistic:

- **Every timestamp crossing this interface is timezone-aware UTC.** The lake
  stores UTC (Phase 3), the cluster's nodes run UTC, and the exchange this
  eventually answers to is America/New_York. A naive datetime here is how a
  backtest crosses a DST seam twice, so the records reject one rather than
  guessing at its zone.
- **Every call names the instant it is asking about, including the live ones.**
  ``quotes()`` takes an ``as_of`` even though a live vendor only ever has
  "now": that is what lets ``ReplayClock`` and ``LiveClock`` drive the same
  code (Phase 4 and Phase 9), and a live provider satisfies it by refusing an
  ``as_of`` that is not approximately now. The alternative - a separate
  historical call - is the seam the two-clock design exists to avoid.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import UTC, date, datetime
from enum import StrEnum
from typing import Protocol, runtime_checkable

__all__ = [
    "CHAINS_ARE_PRICEABLE",
    "Bar",
    "ChainSnapshot",
    "Interval",
    "MarketClosed",
    "MarketDataProvider",
    "MarketSession",
    "OptionQuote",
    "OptionRight",
    "OptionsNotPriceable",
    "Quote",
    "chains_are_priceable_in",
    "require_priceable_chains",
]


class MarketClosed(LookupError):
    """Raised when a call names an instant the exchange was not open for.

    A ``LookupError`` rather than a ``ValueError``: the request was
    well-formed, there is simply nothing there. Phase 3's collectors consult
    ``market_hours()`` before collecting for exactly this reason - the plan's
    rule is "do not collect through a holiday and record silence as data", and
    a provider that returned an empty result instead of raising would make
    silence indistinguishable from a closed market.
    """


class Interval(StrEnum):
    """The bar sizes this interface speaks.

    Deliberately short, and deliberately all divisors of a regular 390-minute
    session: an interval that does not divide the session evenly produces a
    ragged final bar whose semantics every consumer has to special-case. One
    hour is the obvious casualty and is absent for that reason rather than by
    oversight.
    """

    ONE_MINUTE = "1m"
    FIVE_MINUTE = "5m"
    FIFTEEN_MINUTE = "15m"
    THIRTY_MINUTE = "30m"
    ONE_DAY = "1d"

    @property
    def minutes(self) -> int:
        """How many minutes one bar spans. A session, for the daily bar."""
        if self is Interval.ONE_DAY:
            return REGULAR_SESSION_MINUTES
        return int(self.value.removesuffix("m"))

    @property
    def is_intraday(self) -> bool:
        return self is not Interval.ONE_DAY


class OptionRight(StrEnum):
    """Matches ``aerie_trading.db.models.OptionRight`` value for value.

    Two enumerations rather than one import because they are two different
    facts: that one is the Ledger's storage vocabulary, this one is the wire
    vocabulary of the provider interface. They agree today, and if the silo
    ever leaves for its own repository the provider layer should not be the
    thing that drags the schema along with it.
    """

    CALL = "call"
    PUT = "put"


#: The minutes in a regular US equity session, 09:30 to 16:00 Eastern. Early
#: closes are shorter and are read from the calendar rather than from here;
#: this is the divisor ``Interval`` is checked against and the length a full
#: session's bar count is derived from.
REGULAR_SESSION_MINUTES = 390


def _require_utc(value: datetime, field: str) -> datetime:
    """Reject a naive datetime, and normalise an aware one to UTC."""
    if value.tzinfo is None:
        raise ValueError(f"{field} must be timezone-aware; got a naive datetime")
    return value.astimezone(UTC)


@dataclass(frozen=True, slots=True)
class MarketSession:
    """One trading day, with the boundaries the exchange actually used.

    ``is_early_close`` is derived rather than asserted by a caller, because the
    half-days are the ones nobody remembers: the day after Thanksgiving, the
    afternoons before Independence Day and Christmas. A collector that assumes
    16:00 on one of those spends the afternoon recording a flat line.
    """

    session: date
    open: datetime
    close: datetime
    is_early_close: bool

    def __post_init__(self) -> None:
        object.__setattr__(self, "open", _require_utc(self.open, "open"))
        object.__setattr__(self, "close", _require_utc(self.close, "close"))
        if self.close <= self.open:
            raise ValueError(f"session {self.session} closes at or before it opens")

    @property
    def minutes(self) -> int:
        """How long the session ran, in whole minutes."""
        return int((self.close - self.open).total_seconds()) // 60

    def contains(self, moment: datetime) -> bool:
        """Whether ``moment`` falls inside the session.

        Half-open - the closing print belongs to the session, but the instant
        of the close does not open a new bar. ``[open, close)`` is also the
        convention the intraday bar timestamps follow, so "which bar is this
        instant in" has one answer rather than two at every boundary.
        """
        return self.open <= _require_utc(moment, "moment") < self.close


@dataclass(frozen=True, slots=True)
class Bar:
    """One OHLCV bar, timestamped at the instant it *opened*.

    Opening rather than closing timestamps, because that is the convention that
    makes ``no same-bar fills`` (Phase 4's `SimBroker`) expressible without an
    off-by-one: a signal computed on the bar at *t* fills at the open of the
    bar at *t+1*, and both are named by when they began.

    ``adjusted_close`` is stored beside ``close`` because Phase 3's lake layout
    stores both, and a provider that omitted it would leave the collector to
    invent the column. For a source with no corporate actions the two are
    equal, which is a fact about that source rather than an argument for
    dropping the field.
    """

    symbol: str
    interval: Interval
    timestamp: datetime
    open: float
    high: float
    low: float
    close: float
    adjusted_close: float
    volume: int

    def __post_init__(self) -> None:
        object.__setattr__(self, "timestamp", _require_utc(self.timestamp, "timestamp"))
        # The OHLC relationships, asserted at the boundary rather than trusted.
        # docs/plans/trading.md wants a collector or an engine that trips over
        # `high < close` to be tripping over real data rather than over the
        # fixture, which only works if the fixture cannot produce one.
        if self.low > self.high:
            raise ValueError(f"{self.symbol} {self.timestamp}: low {self.low} above high")
        if not (self.low <= self.open <= self.high):
            raise ValueError(f"{self.symbol} {self.timestamp}: open {self.open} outside range")
        if not (self.low <= self.close <= self.high):
            raise ValueError(f"{self.symbol} {self.timestamp}: close {self.close} outside range")
        if self.low <= 0:
            raise ValueError(f"{self.symbol} {self.timestamp}: non-positive price")
        if self.volume < 0:
            raise ValueError(f"{self.symbol} {self.timestamp}: negative volume")


@dataclass(frozen=True, slots=True)
class Quote:
    """Top of book for one instrument at one instant."""

    symbol: str
    timestamp: datetime
    bid: float
    ask: float
    last: float
    bid_size: int
    ask_size: int

    def __post_init__(self) -> None:
        object.__setattr__(self, "timestamp", _require_utc(self.timestamp, "timestamp"))
        if self.ask < self.bid:
            raise ValueError(f"{self.symbol}: crossed quote, ask {self.ask} below bid {self.bid}")
        if self.bid < 0:
            raise ValueError(f"{self.symbol}: negative bid")

    @property
    def mid(self) -> float:
        return (self.bid + self.ask) / 2


@dataclass(frozen=True, slots=True)
class OptionQuote:
    """One option contract in one chain snapshot.

    The full row Phase 3's ``chains/`` partition stores: the contract's
    identity, its market, and the greeks and implied volatility *as the
    provider reported them*. Reported rather than computed is the important
    half - a vendor's own greeks are what its own screens show, and recomputing
    them here would produce a second number that disagrees with the venue for
    reasons nobody can reconstruct a year later.
    """

    symbol: str
    underlying: str
    expiry: date
    strike: float
    right: OptionRight
    timestamp: datetime
    bid: float
    ask: float
    last: float
    volume: int
    open_interest: int
    implied_volatility: float
    delta: float
    gamma: float
    theta: float
    vega: float
    rho: float
    multiplier: int = 100

    def __post_init__(self) -> None:
        object.__setattr__(self, "timestamp", _require_utc(self.timestamp, "timestamp"))
        if self.ask < self.bid:
            raise ValueError(f"{self.symbol}: crossed quote, ask {self.ask} below bid {self.bid}")
        if self.bid < 0 or self.strike <= 0:
            raise ValueError(f"{self.symbol}: non-positive strike or negative bid")
        if self.volume < 0 or self.open_interest < 0:
            raise ValueError(f"{self.symbol}: negative volume or open interest")


@dataclass(frozen=True, slots=True)
class ChainSnapshot:
    """Every contract on one underlying, at one instant.

    A snapshot object rather than a bare sequence because the spot price at the
    moment of the snapshot is part of the observation and is unrecoverable
    afterwards: the underlying's minute bar is a different measurement taken at
    a different instant, and the difference is exactly the width of every
    moneyness calculation anyone will do with this.
    """

    underlying: str
    timestamp: datetime
    spot: float
    contracts: tuple[OptionQuote, ...]

    def __post_init__(self) -> None:
        object.__setattr__(self, "timestamp", _require_utc(self.timestamp, "timestamp"))

    @property
    def expiries(self) -> tuple[date, ...]:
        return tuple(sorted({contract.expiry for contract in self.contracts}))


#: The key a provider's provenance uses to record whether its chains mean
#: anything. Named here rather than spelled as a literal at each end because
#: one end writes it into a ``data_source`` row and the other reads it back out
#: of one, possibly in a different process on a different day.
CHAINS_ARE_PRICEABLE = "chains_are_priceable"


class OptionsNotPriceable(RuntimeError):
    """Refusal: this data source's chains cannot price anything.

    docs/plans/trading.md Phase 2 makes this a guardrail in code rather than a
    convention, for the same reason Phase 6 refuses in the serializer rather
    than in the UI. The synthetic source's chains are structurally valid and
    numerically meaningless - the right shape, noise in every price, greek and
    IV field - which is exactly enough to exercise the collector and the
    partition layout, and exactly the material a plausible-looking wrong answer
    is made of. The distance between "fine for moving bytes" and "meaningless
    for pricing" is where that answer would come from, so nothing is allowed to
    cross it silently.
    """


@runtime_checkable
class ChainPricingClaim(Protocol):
    """The two fields the refusal needs, from whatever is making the claim."""

    @property
    def name(self) -> str: ...

    @property
    def chains_are_priceable(self) -> bool: ...


def require_priceable_chains(source: ChainPricingClaim) -> None:
    """Raise unless ``source``'s option chains are worth pricing against.

    Called by anything that is about to treat an option price as a price:
    Phase 4's engine when a strategy holds an option leg, and Phase 10's
    option backtests. It takes the claim rather than a provider so that the
    check also works from the far side of the lake, where all that survives of
    a provider is a ``data_source`` row - see ``chains_are_priceable_in``.
    """
    if not source.chains_are_priceable:
        raise OptionsNotPriceable(
            f"data source {source.name!r} reports chains that are structurally valid and"
            " numerically meaningless; pricing options against it would produce a"
            " confident wrong answer. See docs/plans/trading.md Phase 2."
        )


def chains_are_priceable_in(provenance: Mapping[str, object]) -> bool:
    """Read the claim back out of a recorded provenance blob.

    **Fails closed.** A provenance that does not say produces ``False``, not a
    shrug: the rows this is asked about were written by a collector that may
    predate the key, and the safe reading of "no idea" is the one that refuses.
    """
    return provenance.get(CHAINS_ARE_PRICEABLE) is True


class MarketDataProvider(Protocol):
    """One source of market data.

    Implementations are constructed with their own configuration and are
    otherwise interchangeable. Phase 3's collectors are written against this
    and nothing else; the plan's acceptance criterion for that phase is that
    none of it changes when Schwab arrives, which is only checkable because
    this file exists first.
    """

    @property
    def name(self) -> str:
        """Stable identifier, and the ``data_source.name`` this registers as."""
        ...

    @property
    def description(self) -> str:
        """One line, for the ``data_source`` row and anything that lists sources."""
        ...

    @property
    def chains_are_priceable(self) -> bool:
        """Whether this source's option prices mean anything. See ``require_priceable_chains``."""
        ...

    @property
    def provenance(self) -> Mapping[str, object]:
        """Everything needed to reproduce this provider's answers.

        Recorded on the ``data_source`` row so that a run is reproducible from
        its provenance alone - which for the synthetic source means the seed,
        the universe and every parameter of the generator, and for a vendor
        means the endpoint and the API version but never the credential.
        """
        ...

    def market_hours(self, start: date, end: date) -> Sequence[MarketSession]:
        """Every session in ``[start, end]``, in order. Holidays are absent."""
        ...

    def bars(
        self,
        symbols: Sequence[str],
        interval: Interval,
        start: datetime,
        end: datetime,
    ) -> Sequence[Bar]:
        """Bars whose opening timestamp falls in ``[start, end)``.

        Ordered by timestamp and then by symbol, so that two calls covering the
        same window are comparable without sorting - which is what Phase 3's
        idempotency gate is checking when it re-runs a collection.
        """
        ...

    def quotes(self, symbols: Sequence[str], as_of: datetime) -> Sequence[Quote]:
        """Top of book for each symbol at ``as_of``.

        Raises ``MarketClosed`` when ``as_of`` is not inside a session. A live
        implementation serves only an ``as_of`` that is approximately now and
        raises otherwise; see this module's docstring for why it takes the
        argument at all.
        """
        ...

    def chain(
        self,
        underlying: str,
        as_of: datetime,
        expiries: Sequence[date] | None = None,
    ) -> ChainSnapshot:
        """Every listed contract on ``underlying`` at ``as_of``.

        ``expiries`` narrows the snapshot to those expiries; ``None`` means the
        whole board. Raises ``MarketClosed`` outside a session.
        """
        ...
