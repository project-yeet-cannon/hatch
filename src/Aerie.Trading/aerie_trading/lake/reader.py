"""The lake's read side: a symbol and a time range in, a polars frame out.

docs/plans/trading.md Phase 3 states this module's whole job and its whole
constraint in one bullet - *"a DuckDB reader with one job: turn a symbol and a
time range into a polars frame, hiding the partition layout from every caller.
The engine must never learn the directory structure."* Everything below is in
service of that second sentence. There is no method here that takes a path, and
there is none that returns one.

**Why DuckDB rather than polars' own scan.** polars can read a Parquet glob
perfectly well, and for the queries this file issues today the two would be
indistinguishable. DuckDB is here for the query that arrives at Phase 4 and
Phase 6: an as-of join between a chain snapshot and the underlying's bar, a
window function over a decade of one symbol, an aggregate across a sweep's
worth of runs - all of which are one SQL statement against files on disk and
none of which fit in a frame that has to be materialised first. Choosing it now
costs nothing and means the engine never has to be ported onto it later.

**The file list is computed, never globbed.** ``layout.py`` can name every
partition a request could touch, so this asks the filesystem only whether those
files exist and hands DuckDB the survivors. Three things follow:

- A read of one month opens one file rather than walking the tree.
- "No data for this symbol" is an empty frame with the right columns, not the
  ``IO Error: No files found that match the pattern`` DuckDB raises for a glob
  that matches nothing - which would make an un-collected range and a mistyped
  symbol the same exception.
- A read cannot accidentally include a partition the layout does not describe,
  such as a writer's leftover temporary file.

**The connection is pinned to UTC**, and that line is load-bearing rather than
tidy. DuckDB renders ``TIMESTAMP WITH TIME ZONE`` in its session timezone,
which defaults to the *host's* - so the same query returns
``datetime[us, America/New_York]`` on a developer's laptop and
``datetime[us, UTC]`` on a cluster node whose nodes run UTC. The instants agree
and the dtypes do not, which is a difference that shows up as a schema mismatch
in a test that passes in CI, or as a comparison against a naive datetime that
silently shifts by five hours. Found on the first read this module ever did.
"""

from __future__ import annotations

import threading
from collections.abc import Mapping, Sequence
from datetime import UTC, date, datetime, timedelta
from pathlib import Path
from types import TracebackType
from typing import Any

import duckdb
import polars as pl

from aerie_trading.lake.layout import (
    bar_partitions,
    chain_snapshot_dir,
    chain_snapshots_on,
    existing,
    normalise_symbol,
)
from aerie_trading.lake.schema import BAR_SCHEMA, CHAIN_SCHEMA, empty_frame
from aerie_trading.providers.base import Interval

__all__ = ["LakeReader"]


class LakeReader:
    """Queries over the lake, with the partition layout kept on this side.

    Cheap to construct - an in-memory DuckDB holding no data, since every query
    reads Parquet off the volume directly - so a caller that wants one per
    request may have one. It is nonetheless a context manager, because the
    connection owns a handful of file descriptors and a long-lived process that
    made one per request without closing them would run out.

    **Not thread-safe on its own, and made so here.** A DuckDB connection
    serialises its own statements but its Python cursor state is per
    connection, so two threads sharing one interleave results. FastAPI runs
    plain ``def`` handlers in a worker thread pool, which makes that a real
    configuration rather than a hypothetical, so the lock is taken around every
    statement. It is not a throughput ceiling worth worrying about: these
    queries are IO against a volume, and the GIL is released for the duration
    of each one.
    """

    def __init__(self, root: Path) -> None:
        self._root = Path(root)
        self._lock = threading.Lock()
        self._connection = duckdb.connect(":memory:")
        # See the module docstring. Set at construction rather than per query
        # so there is exactly one place it can be wrong.
        self._connection.execute("SET TimeZone='UTC'")

    @property
    def root(self) -> Path:
        return self._root

    def __enter__(self) -> LakeReader:
        return self

    def __exit__(
        self,
        exc_type: type[BaseException] | None,
        exc: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        self.close()

    def close(self) -> None:
        with self._lock:
            self._connection.close()

    # -- bars ---------------------------------------------------------------

    def bars(
        self,
        symbols: Sequence[str],
        interval: Interval,
        start: datetime,
        end: datetime,
    ) -> pl.DataFrame:
        """Bars for ``symbols`` at ``interval`` whose timestamp is in ``[start, end)``.

        Half-open, matching ``MarketDataProvider.bars`` and for the same
        reason: consecutive windows tile without either overlapping or leaving
        a hole, so a caller reading a year one month at a time gets each bar
        exactly once.

        Ordered by timestamp then symbol, which is the order the provider
        returns and the order a walk-forward backtest consumes. Sorting in SQL
        rather than in the frame afterwards lets DuckDB do it while it is
        already streaming the rows.
        """
        window_start = _as_utc(start, "start")
        window_end = _as_utc(end, "end")
        if window_end < window_start:
            raise ValueError("end is before start")

        wanted = [normalise_symbol(symbol) for symbol in symbols]
        if not wanted:
            return empty_frame(BAR_SCHEMA)

        files = existing(
            [
                partition.path(self._root)
                for partition in bar_partitions(interval, wanted, window_start, window_end)
            ]
        )
        if not files:
            return empty_frame(BAR_SCHEMA)

        return self._query(
            """
            SELECT *
            FROM read_parquet($files, union_by_name = true)
            WHERE timestamp >= $start AND timestamp < $end
            ORDER BY timestamp, symbol
            """,
            {
                "files": [str(path) for path in files],
                "start": window_start,
                "end": window_end,
            },
            BAR_SCHEMA,
        )

    def bar_coverage(
        self,
        symbols: Sequence[str],
        interval: Interval,
        start: datetime,
        end: datetime,
    ) -> pl.DataFrame:
        """One row per symbol per UTC date, with how many bars are on it.

        The shape a gap check wants: the collector knows which sessions it
        asked for, this says which of them the lake actually holds, and the
        difference is the answer. Computed in SQL because the alternative is
        materialising a year of minute bars in order to count them.
        """
        frame = self.bars(symbols, interval, start, end)
        if frame.is_empty():
            return pl.DataFrame(
                schema={"symbol": pl.String(), "session": pl.Date(), "bars": pl.UInt32()}
            )
        return (
            frame.with_columns(pl.col("timestamp").dt.date().alias("session"))
            .group_by("symbol", "session")
            .agg(pl.len().alias("bars"))
            .sort("symbol", "session")
        )

    # -- chains -------------------------------------------------------------

    def chain_snapshot(self, underlying: str, as_of: datetime) -> pl.DataFrame:
        """The one snapshot of ``underlying`` taken at ``as_of``.

        **Exact, not nearest.** A caller asking for 15:00 on a day the
        collector only reached 14:30 gets an empty frame rather than the 14:30
        board, because handing back the nearest is how a backtest ends up
        pricing a position against a board taken thirty minutes earlier
        without anything in the record saying so. ``snapshot_times`` is how a
        caller finds out what exists; choosing among them is a decision with
        consequences and belongs to whoever is making it.
        """
        moment = _as_utc(as_of, "as_of")
        path = chain_snapshot_dir(self._root, underlying, moment.date()) / (
            f"{moment.strftime('%H%M')}.parquet"
        )
        if not path.is_file():
            return empty_frame(CHAIN_SCHEMA)
        return self._read_chain_files((path,))

    def chain_snapshots(
        self,
        underlying: str,
        start: datetime,
        end: datetime,
    ) -> pl.DataFrame:
        """Every snapshot of ``underlying`` in ``[start, end)``, in one frame.

        Rows carry their own ``timestamp``, so several snapshots in one frame
        are separable by a filter rather than by which file they came out of -
        which is the property that lets a caller ask for a whole session and
        then walk it.
        """
        window_start = _as_utc(start, "start")
        window_end = _as_utc(end, "end")
        if window_end < window_start:
            raise ValueError("end is before start")

        files: list[Path] = []
        day = window_start.date()
        # Inclusive of the end date: `end` is an exclusive instant, but a
        # snapshot earlier the same day is inside the window and its file is
        # in that day's directory.
        while day <= window_end.date():
            files.extend(chain_snapshots_on(self._root, underlying, day))
            day += timedelta(days=1)
        if not files:
            return empty_frame(CHAIN_SCHEMA)

        return self._read_chain_files(
            tuple(files),
            where="WHERE timestamp >= $start AND timestamp < $end",
            extra={"start": window_start, "end": window_end},
        )

    def snapshot_times(self, underlying: str, day: date) -> tuple[datetime, ...]:
        """When ``underlying``'s board was snapshotted on ``day``, in order.

        Read from the file *names*, not from the files: the layout puts the
        instant in the path precisely so that "what do we have" is a directory
        listing rather than a scan of every board on the day. This is the one
        method whose answer depends on the layout, and it returns instants
        rather than paths so the layout still does not leave the module.
        """
        moments: list[datetime] = []
        for path in chain_snapshots_on(self._root, underlying, day):
            slot = path.stem
            moments.append(
                datetime(day.year, day.month, day.day, int(slot[:2]), int(slot[2:]), tzinfo=UTC)
            )
        return tuple(moments)

    def _read_chain_files(
        self,
        files: Sequence[Path],
        where: str = "",
        extra: Mapping[str, object] | None = None,
    ) -> pl.DataFrame:
        parameters: dict[str, object] = {"files": [str(path) for path in files]}
        parameters.update(extra or {})
        return self._query(
            f"""
            SELECT *
            FROM read_parquet($files, union_by_name = true)
            {where}
            ORDER BY timestamp, expiry, option_right, strike
            """,
            parameters,
            CHAIN_SCHEMA,
        )

    # -- the one place a statement runs -------------------------------------

    def _query(
        self,
        sql: str,
        parameters: Mapping[str, object],
        schema: Mapping[str, pl.DataType],
    ) -> pl.DataFrame:
        """Run ``sql`` and hand back a frame conforming to ``schema``.

        The conform step is not cosmetic. ``union_by_name`` fills a column a
        file predates with nulls, and DuckDB types an all-null column as
        ``INTEGER``; casting to the declared schema means a caller's dtype
        checks hold whether or not every partition it read was written by the
        current build. It also puts the columns in the declared order, which
        ``SELECT *`` over a union does not guarantee.
        """
        with self._lock:
            # `params` rather than interpolation, for the ordinary reason and
            # for a specific one: a symbol reaches here from a configured
            # watchlist, and `layout.normalise_symbol` is a whitelist rather
            # than an escape - the two together mean neither a quote nor a
            # path separator can arrive in a statement.
            relation = self._connection.sql(sql, params=dict(parameters))
            frame = pl.DataFrame(_stream(relation))
        return frame.select(
            [
                pl.col(name).cast(dtype)
                if name in frame.columns
                else pl.lit(None, dtype).alias(name)
                for name, dtype in schema.items()
            ]
        )


def _stream(relation: Any) -> Any:
    """The relation, as something polars will consume.

    DuckDB exposes its result through the Arrow C stream PyCapsule interface
    and polars consumes exactly that, so the two hand rows to each other with
    no copy and, notably, **without pyarrow**: ``relation.pl()`` goes through
    ``pyarrow.Table`` and would put a 45 MB wheel in the image to do a
    conversion both libraries can already do natively. The indirection is one
    function so that the reason is written once rather than at every call site,
    and typed loosely because duckdb ships no stub for the capsule protocol.
    """
    return relation


def _as_utc(value: datetime, field: str) -> datetime:
    if value.tzinfo is None:
        raise ValueError(f"{field} must be timezone-aware; got a naive datetime")
    return value.astimezone(UTC)
