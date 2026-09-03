"""The price process: one random walk per symbol, addressable by coordinate.

**What this is deliberately not.** Not a simulator, not a model, and never
going to be one. docs/plans/trading.md is explicit that the generator is pure
noise permanently - the moment it acquires an implied-volatility surface, a
jump model or a regime switch, it stops being a control and starts being a
thing whose own behaviour has to be reasoned about before any result measured
against it means anything. Its job is to have the right *shape*, not the right
*statistics*.

**The walk.** A symbol's session closes are a geometric random walk from its
configured starting price at the calendar's anchor session::

    close[0] = start_price
    close[i] = close[i-1] * exp(mu + sigma * z(i))

where ``z(i)`` is the ``i``-th draw of that symbol's session stream - a pure
function of the seed, the symbol and ``i``, and of nothing about the request.
``mu`` is the drift on the **log** price, which is what makes "drift defaults
to zero" mean "the log price is a martingale". The familiar consequence is that
arithmetic returns then carry a ``+sigma**2/2`` convexity term; that is a
property of the parameterisation rather than a signal, and it is not something
a strategy can time, because the increments remain independent. Independence is
the property the null hypothesis needs, and it is the one asserted by a test.

**Intraday bars are a separate walk, anchored at the session's open, and do not
reconcile with the daily bar.** They share the opening price and nothing else:
the last five-minute close of a session is not the daily close, and the daily
high is not the highest intraday high. This is deliberate and is the one
non-property worth stating out loud.

The only way to make them agree is a Brownian bridge - generate the intraday
path, then pull it onto the session's known terminal price. A bridge's
increments are negatively correlated by construction, because their sum is
constrained, and *zero exploitable autocorrelation* is the single property this
whole source exists to have. Trading a guaranteed structural defect for a
cosmetic agreement between two views of a fictional price is the wrong side of
that trade. Nothing in the plan needs the agreement: Phase 3's collector writes
each interval to its own partition, and Phase 4's engine runs one interval per
backtest.
"""

from __future__ import annotations

import math
from collections.abc import Iterator
from datetime import datetime, timedelta

from aerie_trading.providers.base import Bar, Interval, MarketSession, Quote
from aerie_trading.providers.calendar import MarketCalendar
from aerie_trading.providers.synthetic.config import SyntheticConfig, SyntheticSymbol
from aerie_trading.providers.synthetic.noise import NoiseStream

__all__ = ["SymbolWalk"]

#: How far a wick reaches beyond the bar's body, as a multiple of the bar's own
#: volatility. A shape parameter rather than a modelled one - it exists so that
#: high and low are not merely max and min of open and close, which is a
#: degenerate bar that no real feed produces and that would let a consumer's
#: bug hide.
_WICK_SCALE = 0.45

#: The spread of a bar's volume around the symbol's average, in log space.
_VOLUME_DISPERSION = 0.35

#: Quoted spread as a fraction of price: a floor of a cent plus a few basis
#: points of noise. Structural only; nothing here is a microstructure model.
_SPREAD_FLOOR = 0.01
_SPREAD_BPS = 8.0

#: Slot coordinates are ``session index * this + slot``. Larger than any
#: session's slot count at any supported interval, so two sessions never
#: collide, and constant so that an early close does not shift the
#: coordinates of every session after it.
_SLOT_STRIDE = 1440


def _round_cents(value: float) -> float:
    return round(value, 2)


class SymbolWalk:
    """One symbol's price process, under one configuration.

    Constructed per request rather than cached. The walk to any session is
    ``O(sessions since the anchor)`` of plain float arithmetic - a few thousand
    operations for a decade - so a memo would buy microseconds at the cost of
    the property this source is built on, which is that a value depends on its
    coordinates and on nothing that happened earlier in the process.
    """

    def __init__(
        self,
        config: SyntheticConfig,
        entry: SyntheticSymbol,
        calendar: MarketCalendar,
    ) -> None:
        self._config = config
        self._entry = entry
        self._calendar = calendar

        sessions_per_year = config.sessions_per_year
        volatility = config.volatility_for(entry)
        #: Per-session volatility, from the annualised figure by the square
        #: root of time.
        self._sigma = volatility / math.sqrt(sessions_per_year)
        self._mu = config.drift_for(entry) / sessions_per_year

        root = NoiseStream.named(config.seed, "synthetic", entry.symbol)
        self._session_returns = root.derive("session-return")
        self._daily_wick = root.derive("wick", Interval.ONE_DAY.value)
        self._daily_volume = root.derive("volume", Interval.ONE_DAY.value)
        self._intraday = root.derive("intraday")
        self._spread = root.derive("spread")

    @property
    def symbol(self) -> str:
        return self._entry.symbol

    # -- the trunk ----------------------------------------------------------

    def closes_through(self, index: int) -> list[float]:
        """Session closes from the anchor through session ``index``, inclusive.

        Returned as the whole prefix rather than one value, because every
        caller that wants a price at session ``i`` also wants the price at
        ``i-1`` - a bar opens where the previous one closed - and computing the
        prefix twice is the only way to spend real time here.
        """
        if index < 0:
            raise ValueError(f"session index {index} is before the anchor")
        price = self._entry.start_price
        closes = [price]
        for step in range(1, index + 1):
            price *= math.exp(self._mu + self._sigma * self._session_returns.normal(step))
            closes.append(price)
        return closes

    def close_at(self, index: int) -> float:
        return self.closes_through(index)[index]

    # -- bars ---------------------------------------------------------------

    def daily_bar(self, session: MarketSession, index: int, closes: list[float]) -> Bar:
        """The session's daily bar, from a prefix of closes already computed."""
        opening = closes[index - 1] if index > 0 else self._entry.start_price
        closing = closes[index]
        return self._bar(
            interval=Interval.ONE_DAY,
            timestamp=session.open,
            opening=opening,
            closing=closing,
            step_sigma=self._sigma,
            wick=self._daily_wick,
            wick_index=index,
            volume_stream=self._daily_volume,
            volume_index=index,
            volume_share=1.0,
        )

    def intraday_path(
        self,
        session: MarketSession,
        index: int,
        interval: Interval,
        closes: list[float],
    ) -> list[float]:
        """The closing price of every slot in one session, at an interval.

        The path and the bars are the same walk read two ways - ``price_at``
        needs a level and ``intraday_bars`` needs a bar, and deriving them
        separately is how a quote comes to disagree with the minute bar that
        contains it.

        The slot count comes from the *session's* length rather than from a
        constant, so an early close produces a short session rather than two
        hours of invented afternoon. That is the case the plan calls out as
        failing silently: a generator that ran to 16:00 regardless would leave
        Phase 3's calendar handling untested against exactly the days it is
        needed for.
        """
        count = session.minutes // interval.minutes
        if count == 0:
            return []
        # The session's opening price is the trunk's previous close: the
        # generator has no overnight gaps, because a gap is a corporate action
        # or an earnings print, and it models neither.
        price = closes[index - 1] if index > 0 else self._entry.start_price
        sigma = self._sigma / math.sqrt(count)
        mu = self._mu / count
        steps = self._intraday.derive(interval.value, "step")
        base = index * _SLOT_STRIDE

        path: list[float] = []
        for slot in range(count):
            price *= math.exp(mu + sigma * steps.normal(base + slot))
            path.append(price)
        return path

    def intraday_bars(
        self,
        session: MarketSession,
        index: int,
        interval: Interval,
        closes: list[float],
    ) -> Iterator[Bar]:
        """Every bar of one session at an intraday interval."""
        path = self.intraday_path(session, index, interval, closes)
        if not path:
            return

        span = interval.minutes
        sigma = self._sigma / math.sqrt(len(path))
        # Each (interval, purpose) is its own sub-stream, so adding an interval
        # to the supported set does not renumber the draws of the ones beside
        # it.
        wicks = self._intraday.derive(interval.value, "wick")
        volumes = self._intraday.derive(interval.value, "volume")
        base = index * _SLOT_STRIDE

        opening = closes[index - 1] if index > 0 else self._entry.start_price
        for slot, closing in enumerate(path):
            coordinate = base + slot
            yield self._bar(
                interval=interval,
                timestamp=session.open + timedelta(minutes=span * slot),
                opening=opening,
                closing=closing,
                step_sigma=sigma,
                wick=wicks,
                wick_index=coordinate,
                volume_stream=volumes,
                volume_index=coordinate,
                volume_share=1.0 / len(path),
            )
            opening = closing

    def _bar(
        self,
        *,
        interval: Interval,
        timestamp: datetime,
        opening: float,
        closing: float,
        step_sigma: float,
        wick: NoiseStream,
        wick_index: int,
        volume_stream: NoiseStream,
        volume_index: int,
        volume_share: float,
    ) -> Bar:
        """Assemble one bar, and make the OHLC relationships true by construction.

        Rounding is the subtle half. Prices are rounded to cents because a feed
        quotes cents, but rounding four numbers independently can put the close
        a cent above a high that was a hair below it - so the extremes are
        clamped *after* rounding rather than before. The invariant is then
        arithmetic rather than probabilistic, which matters because ``Bar``
        rejects a violation and the generator is supposed to be the thing that
        cannot produce one.
        """
        body_high = max(opening, closing)
        body_low = min(opening, closing)
        reach = _WICK_SCALE * step_sigma
        high = body_high * math.exp(abs(wick.normal(2 * wick_index)) * reach)
        low = body_low * math.exp(-abs(wick.normal(2 * wick_index + 1)) * reach)

        opening = _round_cents(opening)
        closing = _round_cents(closing)
        rounded_high = max(_round_cents(high), opening, closing)
        rounded_low = min(_round_cents(low), opening, closing)

        # A cent is the floor for every price. A walk with enough sessions
        # behind it can in principle round to zero, and a zero price is not a
        # cheap bar - it is a division by zero in every return calculation
        # downstream.
        floor = 0.01
        opening = max(opening, floor)
        closing = max(closing, floor)
        rounded_high = max(rounded_high, opening, closing)
        rounded_low = max(min(rounded_low, opening, closing), floor)

        volume = int(
            self._entry.average_daily_volume
            * volume_share
            * math.exp(_VOLUME_DISPERSION * volume_stream.normal(volume_index))
        )

        return Bar(
            symbol=self._entry.symbol,
            interval=interval,
            timestamp=timestamp,
            open=opening,
            high=rounded_high,
            low=rounded_low,
            close=closing,
            # No corporate actions in this source, so the adjusted close is the
            # close. The field is carried rather than dropped because Phase 3's
            # lake stores both, and a collector that learned the column did not
            # exist would have to learn it again at Phase 8.
            adjusted_close=closing,
            volume=max(volume, 0),
        )

    # -- instants -----------------------------------------------------------

    def price_at(
        self,
        session: MarketSession,
        index: int,
        moment: datetime,
        closes: list[float],
    ) -> float:
        """The spot price at an instant inside a session.

        Defined as the close of the enclosing one-minute bar, so that a quote,
        a chain snapshot's spot and the minute bar series all agree about the
        same instant. They are one series read three ways rather than three
        series that happen to be near each other.
        """
        path = self.intraday_path(session, index, Interval.ONE_MINUTE, closes)
        if not path:
            return closes[index]
        elapsed = int((moment - session.open).total_seconds()) // 60
        # Rounded, because the bar built from this same slot is rounded: the
        # quote and the minute bar that contains it have to agree to the cent
        # or they are two series rather than one read twice.
        return max(_round_cents(path[min(max(elapsed, 0), len(path) - 1)]), 0.01)

    def quote_at(self, moment: datetime, price: float, index: int) -> Quote:
        """A top-of-book quote around a known price.

        The spread is a floor of one cent plus a few basis points of noise -
        structural, not a microstructure model, and enough that a consumer
        which assumes bid equals ask is caught here rather than at Phase 8.
        """
        half = max(_SPREAD_FLOOR, price * _SPREAD_BPS / 10_000.0) / 2
        jitter = 1.0 + 0.5 * self._spread.uniform(index)
        bid = max(_round_cents(price - half * jitter), 0.01)
        ask = max(_round_cents(price + half * jitter), bid)
        return Quote(
            symbol=self._entry.symbol,
            timestamp=moment,
            bid=bid,
            ask=ask,
            last=_round_cents(price),
            bid_size=self._spread.integer(2 * index, 1, 50) * 100,
            ask_size=self._spread.integer(2 * index + 1, 1, 50) * 100,
        )
