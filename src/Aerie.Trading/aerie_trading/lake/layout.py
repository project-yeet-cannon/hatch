"""Where a row lives on disk. Written down before the lake has data in it.

docs/plans/trading.md Phase 3 is explicit that this file comes first, and the
reason is that rewriting a partition scheme is a migration - of a dataset whose
whole point is that it is the part of the system that cannot be regenerated.

The layout, verbatim from the plan::

    bars/{interval}/{symbol}/{year}/{month}.parquet
    chains/{underlying}/{date}/{hhmm}.parquet

Three properties of it are load-bearing rather than incidental:

- **A bar partition is a month, and a chain partition is a snapshot.** Bars
  arrive one session at a time forever, so a per-session file would produce a
  quarter of a million small files in a decade and a DuckDB scan that spends
  its time opening them. A chain snapshot is already a few thousand rows, is
  written once, and is never appended to - so it is its own file, which is what
  makes writing one idempotent by path alone.
- **The partition keys are also columns** (``schema.py``). Redundant on disk,
  where Parquet's dictionary encoding makes them nearly free, and necessary in
  a frame: a read that spans four symbols hands back one frame, and a caller
  that had to recover the symbol from the file it came out of would be reading
  the directory structure - the exact thing ``reader.py`` exists to hide.
- **Every path this module builds is checked to stay under the root.** A
  symbol is a value from a configured watchlist or from a provider's response,
  which is to say it is not this process's own string; ``..`` or a separator in
  one would otherwise write a Parquet file wherever it liked. The check is a
  character class rather than a ``resolve()`` comparison because it also has to
  reject the shapes that are legal paths and wrong anyway - an empty symbol, a
  leading dot - and because it can then be applied on the read side too, where
  there is no file yet to resolve.

Everything here is stdlib on purpose: this module is the one piece of the lake
that a process holding no polars can still reason about, and the manifests in
``deploy/cluster/trading/lake/`` describe the same tree in words.
"""

from __future__ import annotations

import re
from collections.abc import Iterator, Sequence
from datetime import UTC, date, datetime
from pathlib import Path
from typing import Final

from aerie_trading.providers.base import Interval

__all__ = [
    "BARS_ROOT",
    "CHAINS_ROOT",
    "LAKE_LAYOUT_VERSION",
    "BarPartition",
    "bar_partition",
    "bar_partitions",
    "chain_partition",
    "chain_snapshot_dir",
    "chain_snapshots_on",
    "existing",
    "normalise_symbol",
    "snapshot_slot",
]

#: The two top-level directories, named here so that nothing spells them as a
#: literal twice. A third arrives with the phase that needs it; there is no
#: `quotes/` because nothing collects quotes on a schedule - the interface has
#: the call so that Phase 9's live clock can drive the same code, and a live
#: quote's shelf life is measured in seconds.
BARS_ROOT: Final = "bars"
CHAINS_ROOT: Final = "chains"

#: Bumped when the *tree* changes shape - a new directory level, a renamed
#: partition key, a different file granularity. Not bumped when a column is
#: added to ``schema.py``, which Parquet handles by itself. It is written into
#: no file: a layout version stored inside the partitions it describes is only
#: readable once you already know where they are. It exists so that a migration
#: written later has a number to name, and so that this comment has somewhere
#: to live.
LAKE_LAYOUT_VERSION: Final = 1

# Uppercase alphanumerics, and the three punctuation marks a real ticker or an
# OCC contract symbol can carry: `.` (BRK.B), `-` (a class suffix on some
# venues) and `+` (a warrant). No `/`, no `\`, no `..`, nothing that is a path
# operator on any filesystem this runs on. Anchored at both ends, and the first
# character is deliberately narrower than the rest so that a leading dot cannot
# produce a hidden directory or a relative path component.
_SYMBOL = re.compile(r"^[A-Z0-9][A-Z0-9.+-]{0,31}$")


def normalise_symbol(symbol: str) -> str:
    """Upper-case ``symbol``, or refuse it.

    A ``ValueError`` rather than a sanitising rewrite. Stripping the offending
    characters would turn a typo into a partition that looks legitimate and
    holds the wrong instrument's data, which is the failure this whole silo is
    built to make impossible - and the caller passing it is either a
    configured watchlist or a provider's response, so both ends of the mistake
    are worth hearing about.
    """
    candidate = symbol.strip().upper()
    if not _SYMBOL.match(candidate):
        raise ValueError(
            f"{symbol!r} is not a usable symbol: a lake partition is named after it,"
            " so it must be 1-32 characters of A-Z, 0-9, '.', '-' or '+'"
        )
    return candidate


class BarPartition:
    """One ``bars/`` file's coordinates: an interval, a symbol and a month.

    A class rather than a tuple because it is the key a write is grouped by and
    the name that appears in a ``WriteReport``, and both of those read better
    with fields than with ``key[2]``.
    """

    __slots__ = ("interval", "month", "symbol", "year")

    def __init__(self, interval: Interval, symbol: str, year: int, month: int) -> None:
        if not 1 <= month <= 12:
            raise ValueError(f"month {month} is not a month")
        if not 1 <= year <= 9999:
            raise ValueError(f"year {year} is outside the range a path can name")
        self.interval = interval
        self.symbol = normalise_symbol(symbol)
        self.year = year
        self.month = month

    def __eq__(self, other: object) -> bool:
        if not isinstance(other, BarPartition):
            return NotImplemented
        return self._key() == other._key()

    def __hash__(self) -> int:
        return hash(self._key())

    def __repr__(self) -> str:
        return (
            f"BarPartition({self.interval.value}, {self.symbol}, {self.year:04d}-{self.month:02d})"
        )

    def _key(self) -> tuple[str, str, int, int]:
        return (self.interval.value, self.symbol, self.year, self.month)

    def path(self, root: Path) -> Path:
        """This partition's file, whether or not it exists."""
        return (
            root
            / BARS_ROOT
            / self.interval.value
            / self.symbol
            / f"{self.year:04d}"
            / f"{self.month:02d}.parquet"
        )

    @classmethod
    def containing(cls, interval: Interval, symbol: str, moment: datetime) -> BarPartition:
        """The partition a bar timestamped ``moment`` belongs in.

        The month is taken in **UTC**, because that is what the timestamp
        column stores and the only frame in which "which file is this row in"
        has one answer. A session that opens on 30 April Eastern and a
        partition boundary in local time would disagree about which file its
        bars land in twice a year, which is the sort of thing discovered as a
        gap in a backtest rather than as an error.
        """
        utc = _as_utc(moment)
        return cls(interval, symbol, utc.year, utc.month)


def bar_partition(root: Path, interval: Interval, symbol: str, moment: datetime) -> Path:
    """The file one bar belongs in. The common case, in one call."""
    return BarPartition.containing(interval, symbol, moment).path(root)


def bar_partitions(
    interval: Interval,
    symbols: Sequence[str],
    start: datetime,
    end: datetime,
) -> tuple[BarPartition, ...]:
    """Every partition a ``[start, end)`` read could touch, in order.

    Enumerated arithmetically rather than by globbing the tree, which is what
    lets ``reader.py`` name its inputs exactly: DuckDB raises on a glob that
    matches nothing, so a reader built on globs has to treat "this symbol has
    no data" and "this path is wrong" as the same IO error. Enumerating and
    then filtering by existence keeps them distinguishable, and keeps a read
    of one month off a directory walk of the whole lake.

    Half-open at the end, matching ``MarketDataProvider.bars``. An ``end`` that
    falls exactly on a month boundary therefore does not pull in the month it
    names, because no row in that window can be in it.
    """
    first = _as_utc(start)
    last = _as_utc(end)
    if last < first:
        raise ValueError("end is before start")

    months: list[tuple[int, int]] = []
    year, month = first.year, first.month
    # `end` is exclusive, so the final month is the one containing the last
    # instant that can be in the window rather than the one containing `end`
    # itself - otherwise every read ending at midnight on the first opens an
    # empty file it did not need.
    stop_year, stop_month = last.year, last.month
    if last.day == 1 and (last.hour, last.minute, last.second, last.microsecond) == (0, 0, 0, 0):
        stop_year, stop_month = _previous_month(stop_year, stop_month)
    while (year, month) <= (stop_year, stop_month):
        months.append((year, month))
        year, month = _next_month(year, month)

    return tuple(
        BarPartition(interval, symbol, year, month)
        for symbol in _unique(symbols)
        for year, month in months
    )


def snapshot_slot(moment: datetime) -> str:
    """The ``hhmm`` a chain snapshot is filed under, in UTC.

    Minute resolution, which fixes the finest interval a snapshot schedule can
    use. That is a decision rather than a default: two snapshots of the same
    underlying inside one minute would collide on this name, and the second
    would silently replace the first. A collection interval measured in
    minutes is the one the plan asks for ("a conservative interval"), a venue's
    board does not move meaningfully inside a minute, and a seconds-resolution
    name would put four more characters in every path in the largest half of
    the lake to encode a case nothing wants.
    """
    return _as_utc(moment).strftime("%H%M")


def chain_snapshot_dir(root: Path, underlying: str, day: date) -> Path:
    """The directory holding one underlying's snapshots for one UTC date."""
    return root / CHAINS_ROOT / normalise_symbol(underlying) / day.isoformat()


def chain_partition(root: Path, underlying: str, moment: datetime) -> Path:
    """The file one chain snapshot is written to.

    The date and the time are both taken in UTC, so a snapshot's directory is
    its timestamp's UTC date and not its session's Eastern one. Those differ
    for no US equity session - one runs 13:30-21:00 UTC and never crosses a UTC
    midnight - which is exactly why UTC is safe here and why it is still worth
    stating: the property is a fact about this exchange, so a venue that trades
    through midnight UTC needs this decision revisited rather than inherited.
    """
    utc = _as_utc(moment)
    return chain_snapshot_dir(root, underlying, utc.date()) / f"{snapshot_slot(utc)}.parquet"


def chain_snapshots_on(root: Path, underlying: str, day: date) -> tuple[Path, ...]:
    """Every snapshot file already written for ``underlying`` on ``day``.

    Sorted by name, which for ``hhmm`` is sorted by time. Empty when the
    directory does not exist, which is the honest answer for a day nothing was
    collected on - the collector is where a missing session is refused rather
    than reported as emptiness (``collect/chains.py``), and a reader that
    raised here would make an un-collected day indistinguishable from a
    mistyped symbol.
    """
    directory = chain_snapshot_dir(root, underlying, day)
    if not directory.is_dir():
        return ()
    return tuple(sorted(directory.glob("[0-9][0-9][0-9][0-9].parquet")))


def existing(paths: Sequence[Path]) -> tuple[Path, ...]:
    """The subset of ``paths`` that are files on disk, order preserved."""
    return tuple(path for path in paths if path.is_file())


def _unique(values: Sequence[str]) -> Iterator[str]:
    """Normalised symbols, de-duplicated, in the order first seen."""
    seen: set[str] = set()
    for value in values:
        symbol = normalise_symbol(value)
        if symbol not in seen:
            seen.add(symbol)
            yield symbol


def _next_month(year: int, month: int) -> tuple[int, int]:
    return (year + 1, 1) if month == 12 else (year, month + 1)


def _previous_month(year: int, month: int) -> tuple[int, int]:
    return (year - 1, 12) if month == 1 else (year, month - 1)


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        raise ValueError("a lake path is derived from an instant; got a naive datetime")
    return value.astimezone(UTC)
