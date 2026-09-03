"""Bars, aligned onto one timeline, with everything after the cursor withheld.

This is where Phase 4's lookahead gate is actually enforced. The engine's rule
is that a strategy on the bar at *t* can see every bar at or before *t* and
nothing after it, and the way to make that true is not to remind strategies of
it - it is to hand them an object that has no method returning a later bar.
So ``BarHistory`` is immutable and stateless about *when* it is; every accessor
takes the cursor index explicitly, and every one of them slices to it. The
cursor lives on ``StrategyContext``, which the engine constructs once per tick.

**The timeline is the union of the timestamps the bars actually have**, not a
generated schedule. Two consequences, both wanted:

- A symbol with no bar at some instant simply has no bar there. Nothing is
  carried forward, nothing is interpolated, and a strategy that wants the last
  known price asks for it (``latest``) rather than being handed one that looks
  like a fresh observation.
- A session missing from the lake is a session the run does not visit at all,
  which is the honest reading of a gap. The alternative - ticking anyway - is
  how a backtest trades through a halt.

**Multi-symbol from the start**, because both reference strategies run over the
whole configured universe rather than over one name, and because a single-
symbol engine is the other retrofit this plan is written to avoid.

The lake is reached from here and from nowhere else in ``engine``. See the
package docstring: polars and duckdb are 90 MB of wheel and the fixture test
that proves the accounting to the cent builds its bars by hand.
"""

from __future__ import annotations

import bisect
import hashlib
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from decimal import Decimal

from aerie_trading.engine.money import money
from aerie_trading.providers.base import Bar, Interval

__all__ = ["BarHistory", "load_history"]


@dataclass(frozen=True, slots=True)
class BarHistory:
    """Every bar a run may read, indexed by a position on a shared timeline.

    Constructed through ``from_bars`` or ``load_history`` rather than directly:
    the three internal structures have to agree with each other, and a caller
    who built them by hand could produce a history whose cutoffs point past its
    own series.
    """

    interval: Interval
    symbols: tuple[str, ...]
    timeline: tuple[datetime, ...]
    #: Per symbol, its bars in timestamp order.
    _series: Mapping[str, tuple[Bar, ...]]
    #: Per symbol, per timeline index, how many of that symbol's bars are at or
    #: before that instant. Precomputed because it is the answer to every
    #: question below and recomputing it by scanning would make a lookback of
    #: 200 bars an O(n) walk on every tick of every run of every sweep.
    _cutoffs: Mapping[str, tuple[int, ...]]

    # -- construction --------------------------------------------------------

    @classmethod
    def from_bars(cls, bars: Sequence[Bar]) -> BarHistory:
        """Group ``bars`` by symbol and build the timeline they imply.

        Refuses a mixed-interval sequence. A daily bar and a minute bar on one
        timeline is not a richer history - it is two different meanings of
        "the bar at 14:30" sharing an index, and every moving average computed
        over the result is arithmetic on incomparable numbers.
        """
        if not bars:
            raise ValueError("a history needs at least one bar")
        intervals = {bar.interval for bar in bars}
        if len(intervals) != 1:
            raise ValueError(
                f"a history holds one interval; got {sorted(value.value for value in intervals)}"
            )
        interval = next(iter(intervals))

        grouped: dict[str, list[Bar]] = {}
        for bar in bars:
            grouped.setdefault(bar.symbol, []).append(bar)

        series: dict[str, tuple[Bar, ...]] = {}
        for symbol, symbol_bars in grouped.items():
            ordered = tuple(sorted(symbol_bars, key=lambda bar: bar.timestamp))
            stamps = [bar.timestamp for bar in ordered]
            if len(set(stamps)) != len(stamps):
                raise ValueError(
                    f"{symbol}: two bars share a timestamp at {interval.value}."
                    " A duplicate is usually a lake partition read twice."
                )
            series[symbol] = ordered

        timeline = tuple(sorted({bar.timestamp for bar in bars}))
        # The stamp list is hoisted out of the inner loop rather than rebuilt
        # per instant: a decade of minute bars is a quarter of a million
        # timeline positions, and rebuilding the list inside the comprehension
        # would make construction quadratic in the length of the run.
        cutoffs: dict[str, tuple[int, ...]] = {}
        for symbol, ordered in series.items():
            stamps = [bar.timestamp for bar in ordered]
            cutoffs[symbol] = tuple(bisect.bisect_right(stamps, instant) for instant in timeline)
        return cls(
            interval=interval,
            symbols=tuple(sorted(series)),
            timeline=timeline,
            _series=series,
            _cutoffs=cutoffs,
        )

    # -- reading, always sliced to the cursor --------------------------------

    def __len__(self) -> int:
        return len(self.timeline)

    def index_of(self, instant: datetime) -> int:
        """Where ``instant`` sits on the timeline. Raises if it is not on it."""
        moment = instant.astimezone(UTC)
        position = bisect.bisect_left(self.timeline, moment)
        if position >= len(self.timeline) or self.timeline[position] != moment:
            raise LookupError(f"{moment.isoformat()} is not on this history's timeline")
        return position

    def bar_at(self, symbol: str, index: int) -> Bar | None:
        """``symbol``'s bar at exactly ``timeline[index]``, or ``None``.

        Exact rather than nearest, for the reason ``LakeReader.chain_snapshot``
        gives about snapshots: handing back a neighbour is how a fill happens
        against a price from another minute with nothing in the record saying
        so. A caller that wants the most recent known bar asks ``latest``, and
        by asking it says so.
        """
        series = self._series.get(symbol)
        if series is None:
            return None
        cutoff = self._cutoffs[symbol][index]
        if cutoff == 0:
            return None
        candidate = series[cutoff - 1]
        return candidate if candidate.timestamp == self.timeline[index] else None

    def latest(self, symbol: str, index: int) -> Bar | None:
        """``symbol``'s most recent bar at or before ``timeline[index]``."""
        series = self._series.get(symbol)
        if series is None:
            return None
        cutoff = self._cutoffs[symbol][index]
        return series[cutoff - 1] if cutoff else None

    def window(self, symbol: str, index: int, count: int | None = None) -> tuple[Bar, ...]:
        """``symbol``'s last ``count`` bars at or before ``timeline[index]``.

        ``count`` of ``None`` is everything so far. Fewer than ``count`` bars
        is a legitimate answer and is not padded: a strategy warming up a
        200-bar average has to notice that it does not have 200 bars yet, and
        a padded window would let it compute a confident average over invented
        history instead.
        """
        series = self._series.get(symbol)
        if series is None:
            return ()
        cutoff = self._cutoffs[symbol][index]
        if count is None:
            return series[:cutoff]
        if count <= 0:
            raise ValueError("a lookback of zero or fewer bars is not a lookback")
        return series[max(0, cutoff - count) : cutoff]

    # -- identity ------------------------------------------------------------

    def fingerprint(self) -> str:
        """A sha256 over every bar this history holds - "the lake state it read".

        Phase 5's ``run`` row stores this beside the result fingerprint, and the
        pair is what makes a disagreement between two runs diagnosable rather
        than merely visible: same data and different results is a determinism
        bug in the engine, different data and different results is a lake that
        was rewritten underneath them. A run window and an interval cannot
        answer that on their own, because a partition re-collected after a
        provider correction covers the identical window with different numbers.

        Every field is rendered as a string for the reason
        ``BacktestResult.canonical`` gives - a float on its way through JSON is
        a binary round trip, and a fingerprint that survives one is a
        fingerprint that would also survive the change it exists to catch.
        Iteration is over ``symbols``, which is sorted, so two histories built
        from the same bars in different orders agree.
        """
        digest = hashlib.sha256()
        digest.update(self.interval.value.encode("utf-8"))
        for symbol in self.symbols:
            digest.update(b"\x00")
            digest.update(symbol.encode("utf-8"))
            for bar in self._series[symbol]:
                digest.update(
                    "|".join(
                        (
                            bar.timestamp.isoformat(),
                            repr(bar.open),
                            repr(bar.high),
                            repr(bar.low),
                            repr(bar.close),
                            repr(bar.adjusted_close),
                            str(bar.volume),
                        )
                    ).encode("utf-8")
                )
        return digest.hexdigest()

    def opens_at(self, index: int) -> Mapping[str, Decimal]:
        """Opening price of every bar that exists at exactly ``timeline[index]``.

        What the broker fills against. Exact rather than carried forward: an
        order for a symbol with no bar here does not fill (see
        ``SimBroker.fill_at``), which is the correct behaviour for a halt.
        """
        prices: dict[str, Decimal] = {}
        for symbol in self.symbols:
            bar = self.bar_at(symbol, index)
            if bar is not None:
                prices[symbol] = money(bar.open)
        return prices

    def marks_at(self, index: int) -> Mapping[str, Decimal]:
        """Closing price of the most recent bar of each symbol at or before ``index``.

        Carried forward here, unlike ``opens_at``, and the asymmetry is
        deliberate: valuing a position at the last price it traded at is what a
        statement does, while *trading* at a stale price is a fill that never
        happened.
        """
        prices: dict[str, Decimal] = {}
        for symbol in self.symbols:
            bar = self.latest(symbol, index)
            if bar is not None:
                prices[symbol] = money(bar.close)
        return prices


def load_history(
    reader: object,
    symbols: Sequence[str],
    interval: Interval,
    start: datetime,
    end: datetime,
) -> BarHistory:
    """Read ``symbols`` over ``[start, end)`` out of the lake, as a ``BarHistory``.

    ``reader`` is typed loosely on purpose: ``LakeReader`` is imported inside
    the function so that importing ``engine.history`` does not drag duckdb into
    a process that only wanted to construct bars by hand. Every caller in the
    silo passes a ``LakeReader``, and the duck typing is checked by the one
    attribute this touches.
    """
    from aerie_trading.lake.reader import LakeReader

    if not isinstance(reader, LakeReader):
        raise TypeError("load_history needs a LakeReader")
    frame = reader.bars(symbols, interval, start, end)
    if frame.is_empty():
        raise LookupError(
            f"the lake holds no {interval.value} bars for {sorted(symbols)} in"
            f" [{start.isoformat()}, {end.isoformat()}). Collect before backtesting."
        )
    bars = [
        Bar(
            symbol=str(row["symbol"]),
            interval=Interval(str(row["interval"])),
            timestamp=row["timestamp"],
            open=float(row["open"]),
            high=float(row["high"]),
            low=float(row["low"]),
            close=float(row["close"]),
            adjusted_close=float(row["adjusted_close"]),
            volume=int(row["volume"]),
        )
        for row in frame.iter_rows(named=True)
    ]
    return BarHistory.from_bars(bars)
