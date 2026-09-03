"""The ``Clock`` protocol, and the one that walks history.

docs/plans/trading.md's *One engine, two clocks*: ``ReplayClock`` walks
historical timestamps as fast as the CPU allows, ``LiveClock`` ticks on wall
time as quotes arrive, and *"a strategy sees the same context either way"*.
Phase 4's requirement for this file is stated as a constraint on Phase 9 -
**``LiveClock`` must require no change here when it arrives** - so the surface
below is the smallest one that both can honestly implement.

Two calls and one property, which is the whole interface:

===============  ==========================================================
``now``          the instant the engine is currently reasoning about.
``advance()``    move to the next instant, or report that there is none.
``ticks()``      iterate the instants, which is ``advance`` as a loop.
===============  ==========================================================

**Why ``now`` is a property of the clock rather than an argument threaded
through the engine.** A strategy asks the context for the time, the context
asks the clock, and there is exactly one answer at any point in the run. The
alternative - each component keeping its own idea of the current instant -
is the shape that lets a broker fill at one time while the portfolio marks at
another, which is a class of bug that produces a plausible equity curve and no
error message.

**Why ``advance()`` returns the new instant rather than a bool.** A live clock
blocks until the next tick and then knows what time it is; a replay clock
looks it up. Returning it means neither has to be asked twice, and ``None``
distinguishes "the data ran out" from "time has not moved", which a live clock
needs to say without lying about the wall clock.

**A clock never runs backwards, and ``ReplayClock`` refuses to be built from
timestamps that would make it.** Out-of-order or duplicated instants are not a
hypothetical: they are what a naive concatenation of two lake partitions
produces, and an engine that walked them would fill an order in the past.
"""

from __future__ import annotations

from collections.abc import Iterator, Sequence
from datetime import UTC, datetime
from typing import Protocol, runtime_checkable

__all__ = ["Clock", "ReplayClock"]


@runtime_checkable
class Clock(Protocol):
    """The engine's only source of "what time is it".

    Implementations are stateful and are driven by exactly one engine loop.
    Sharing one between two runs is a bug the type system cannot catch, which
    is why ``Backtest`` constructs its own rather than accepting one that has
    already been started.
    """

    @property
    def now(self) -> datetime:
        """The current instant, timezone-aware UTC.

        Raises ``LookupError`` before the first ``advance()``. A clock that
        answered with the epoch, or with the first tick, would let a component
        that forgot to start the loop produce numbers instead of an error.
        """
        ...

    @property
    def started(self) -> bool:
        """Whether ``advance()`` has been called at least once."""
        ...

    def advance(self) -> datetime | None:
        """Move to the next instant, or ``None`` when there are no more."""
        ...

    def ticks(self) -> Iterator[datetime]:
        """Every remaining instant, in order. ``advance()`` as a loop."""
        ...


class ReplayClock:
    """Walks a fixed sequence of instants as fast as the caller consumes them.

    The sequence comes from the data rather than from a schedule: the engine
    ticks on the union of the timestamps its bars actually have (see
    ``engine.history``), so a session the lake is missing is a session the
    clock does not visit. That is deliberate and is the honest behaviour -
    inventing a tick for a day with no bars would ask every strategy to
    decide what a bar-less bar means, and the answer they would all reach for
    is "carry the last price forward", which is how a backtest quietly trades
    through a halt.
    """

    __slots__ = ("_index", "_instants")

    def __init__(self, instants: Sequence[datetime]) -> None:
        checked: list[datetime] = []
        previous: datetime | None = None
        for instant in instants:
            if instant.tzinfo is None:
                raise ValueError("clock instants must be timezone-aware; got a naive datetime")
            moment = instant.astimezone(UTC)
            if previous is not None and moment <= previous:
                raise ValueError(
                    f"clock instants must strictly increase; {moment.isoformat()} follows"
                    f" {previous.isoformat()}. A duplicated or out-of-order timestamp is"
                    " usually two lake partitions concatenated without a sort."
                )
            checked.append(moment)
            previous = moment
        self._instants: tuple[datetime, ...] = tuple(checked)
        # -1 rather than 0 so that `now` is unanswerable until the first
        # advance, which is what makes "the loop was never started" an
        # exception rather than a silent read of the first bar.
        self._index = -1

    def __len__(self) -> int:
        return len(self._instants)

    @property
    def instants(self) -> tuple[datetime, ...]:
        """Every instant this clock will visit, including the ones already past."""
        return self._instants

    @property
    def started(self) -> bool:
        return self._index >= 0

    @property
    def now(self) -> datetime:
        if self._index < 0:
            raise LookupError("the clock has not been advanced; there is no current instant")
        return self._instants[self._index]

    @property
    def remaining(self) -> int:
        """How many instants are still ahead. Zero on the final bar."""
        return len(self._instants) - self._index - 1

    def advance(self) -> datetime | None:
        if self._index + 1 >= len(self._instants):
            return None
        self._index += 1
        return self._instants[self._index]

    def ticks(self) -> Iterator[datetime]:
        while (instant := self.advance()) is not None:
            yield instant
