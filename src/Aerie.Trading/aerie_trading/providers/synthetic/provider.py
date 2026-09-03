"""``SyntheticMarketDataProvider`` - the interface's first implementation.

This is the class docs/plans/trading.md re-cut the whole plan around. Under the
original order every phase after the interface waited behind a Schwab approval;
built against this instead, the collector, the lake, the engine and the sweeps
are all written and hardened against a source that needs no credential, no
network and no market being open, and Phase 8 becomes a second class landing in
machinery that already works.

It is a composition and almost nothing else. ``calendar`` knows when the market
is open, ``prices`` knows what a symbol did, ``chains`` knows what a board looks
like, and this class is the four methods that turn a request into coordinates
for them. That shape is the point: when the Schwab provider is written it will
be the same four methods over an HTTP client, and anything clever that had
accumulated here would be a thing to reimplement rather than to replace.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from datetime import UTC, date, datetime

from aerie_trading.providers.base import (
    CHAINS_ARE_PRICEABLE,
    Bar,
    ChainSnapshot,
    Interval,
    MarketSession,
    Quote,
)
from aerie_trading.providers.calendar import MarketCalendar, get_market_calendar
from aerie_trading.providers.synthetic.chains import build_chain
from aerie_trading.providers.synthetic.config import (
    SYNTHETIC_GENERATOR_VERSION,
    SyntheticConfig,
)
from aerie_trading.providers.synthetic.prices import SymbolWalk

__all__ = ["SYNTHETIC_SOURCE_NAME", "SyntheticMarketDataProvider"]

#: The ``data_source.name`` this registers under, and the value every row in
#: the lake carries. Fixed rather than derived from the configuration: a
#: re-seeded generator is the same *source* producing different numbers, and
#: which numbers is what the provenance blob is for.
SYNTHETIC_SOURCE_NAME = "synthetic"


class SyntheticMarketDataProvider:
    """Market data for a market that does not exist."""

    def __init__(self, config: SyntheticConfig | None = None) -> None:
        self._config = config if config is not None else SyntheticConfig()
        self._calendar = get_market_calendar(self._config.calendar, self._config.anchor)

    @property
    def config(self) -> SyntheticConfig:
        return self._config

    @property
    def calendar(self) -> MarketCalendar:
        return self._calendar

    @property
    def name(self) -> str:
        return SYNTHETIC_SOURCE_NAME

    @property
    def description(self) -> str:
        return (
            "Deterministic synthetic market data. Zero alpha by construction;"
            " option chains are structurally valid and numerically meaningless."
        )

    @property
    def chains_are_priceable(self) -> bool:
        """No, and this is the guardrail rather than a disclaimer.

        ``require_priceable_chains`` turns this into a refusal at every point
        that is about to treat one of these option prices as a price. The bars
        are a legitimate null hypothesis - a driftless random walk is a real
        thing to measure a strategy against - and the chains are noise wearing
        the right shape, which is a different claim entirely.
        """
        return False

    @property
    def provenance(self) -> Mapping[str, object]:
        """Enough to reproduce every number this provider will ever return.

        Which is the whole configuration and the generator's contract version,
        and nothing else: there is no endpoint, no credential and no clock in
        the answer, because a value that depends on any of those would be a
        value the record could not reproduce.
        """
        return {
            "provider": SYNTHETIC_SOURCE_NAME,
            "generator_version": SYNTHETIC_GENERATOR_VERSION,
            CHAINS_ARE_PRICEABLE: False,
            "config": self._config.model_dump(mode="json"),
        }

    # -- the interface ------------------------------------------------------

    def market_hours(self, start: date, end: date) -> Sequence[MarketSession]:
        return self._calendar.sessions_in_range(start, end)

    def bars(
        self,
        symbols: Sequence[str],
        interval: Interval,
        start: datetime,
        end: datetime,
    ) -> Sequence[Bar]:
        """Every bar in ``[start, end)`` for each symbol.

        Half-open at the end so that consecutive windows tile without
        overlapping. Phase 3 re-runs collections over adjacent windows and
        asserts no duplicate rows; a closed interval would put the boundary bar
        in both of them, and the collector would be blamed for it.
        """
        window_start = _as_utc(start, "start")
        window_end = _as_utc(end, "end")
        if window_end < window_start:
            raise ValueError("end is before start")

        sessions = self._calendar.sessions_in_range(window_start.date(), window_end.date())
        if not sessions:
            return ()

        first = self._calendar.index_of(sessions[0].session)
        last = self._calendar.index_of(sessions[-1].session)

        collected: list[Bar] = []
        for symbol in dict.fromkeys(name.upper() for name in symbols):
            walk = SymbolWalk(self._config, self._config.symbol(symbol), self._calendar)
            # One trunk per symbol per request, covering the whole window. The
            # walk to a session is O(sessions since the anchor), so computing
            # it once and indexing into it is the difference between a
            # backfill that is linear in the window and one that is quadratic.
            closes = walk.closes_through(last)
            for offset, session in enumerate(sessions):
                index = first + offset
                if interval is Interval.ONE_DAY:
                    produced: tuple[Bar, ...] = (walk.daily_bar(session, index, closes),)
                else:
                    produced = tuple(walk.intraday_bars(session, index, interval, closes))
                collected.extend(
                    bar for bar in produced if window_start <= bar.timestamp < window_end
                )

        collected.sort(key=lambda bar: (bar.timestamp, bar.symbol))
        return tuple(collected)

    def quotes(self, symbols: Sequence[str], as_of: datetime) -> Sequence[Quote]:
        moment = _as_utc(as_of, "as_of")
        session = self._calendar.session_containing(moment)
        index = self._calendar.index_of(session.session)
        minute = int((moment - session.open).total_seconds()) // 60

        quoted: list[Quote] = []
        for symbol in dict.fromkeys(name.upper() for name in symbols):
            walk = SymbolWalk(self._config, self._config.symbol(symbol), self._calendar)
            closes = walk.closes_through(index)
            price = walk.price_at(session, index, moment, closes)
            quoted.append(walk.quote_at(moment, price, index * 1440 + minute))
        quoted.sort(key=lambda quote: quote.symbol)
        return tuple(quoted)

    def chain(
        self,
        underlying: str,
        as_of: datetime,
        expiries: Sequence[date] | None = None,
    ) -> ChainSnapshot:
        moment = _as_utc(as_of, "as_of")
        symbol = underlying.upper()
        entry = self._config.symbol(symbol)
        if not entry.has_options:
            # A ``KeyError`` for the same reason an unknown symbol is one: the
            # request named something that does not exist, and answering with
            # an empty board would be indistinguishable from a board that
            # happens to be empty today.
            raise KeyError(f"{symbol!r} has no options board in the synthetic universe")

        session = self._calendar.session_containing(moment)
        index = self._calendar.index_of(session.session)
        walk = SymbolWalk(self._config, entry, self._calendar)
        closes = walk.closes_through(index)
        spot = walk.price_at(session, index, moment, closes)

        return build_chain(
            self._config,
            self._calendar,
            symbol,
            moment,
            spot,
            tuple(expiries) if expiries is not None else None,
        )


def _as_utc(value: datetime, field: str) -> datetime:
    if value.tzinfo is None:
        raise ValueError(f"{field} must be timezone-aware; got a naive datetime")
    return value.astimezone(UTC)
