"""Writing into the lake, idempotently, and surviving being killed mid-write.

docs/plans/trading.md Phase 3 asks for two properties and gates on both:

- *"A re-run over the same window overwrites its own partition and does not
  append duplicates. Assume the collector will be re-run; make that boring."*
- *"a deliberately killed mid-collection run leaves no duplicate rows on
  re-run"*

They are different properties and they need different mechanisms, which is the
one thing worth understanding about this file.

**Idempotency is a merge, not an overwrite.** A bar partition is a month and a
collection is a session, so "overwrite the partition" would delete twenty
sessions to rewrite one. What happens instead is an anti-join on
``BAR_KEY_COLUMNS``: the rows the incoming frame names are removed from what is
already there, the incoming rows are put in their place, and everything else is
left alone. Re-running a session replaces exactly that session; running the
next one adds to it. Both are the same code path, which is what makes the
backfill and the incremental collector the same collector with different
arguments.

**Crash-safety is an atomic rename.** Every write goes to a temporary file in
the destination's own directory, is flushed to the platter, and is then moved
onto the destination with ``os.replace`` - which is atomic on POSIX within a
filesystem, and a filesystem is what the sibling-directory requirement buys.
A process killed at any point leaves either the previous file or the new one,
never a half-written Parquet footer, so the re-run has something valid to merge
against rather than a file that raises on read.

The temporary file's own name carries the pid, because two collectors writing
different symbols into the same tree at once is normal and two of them picking
the same temporary name is not.
"""

from __future__ import annotations

import logging
import os
import tempfile
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Literal

import polars as pl

from aerie_trading.lake.layout import BarPartition, chain_partition
from aerie_trading.lake.schema import (
    BAR_KEY_COLUMNS,
    BAR_SCHEMA,
    Provenance,
    bars_frame,
    chain_frame,
)
from aerie_trading.providers.base import Bar, ChainSnapshot

__all__ = ["LakeWriter", "WriteReport"]

logger = logging.getLogger(__name__)


#: The compressions polars will accept, spelled here rather than imported from
#: ``polars._typing``. A private module is a dependency on an implementation
#: detail; this is a dependency on a documented set of values, and if polars
#: ever removes one the failure is a type error at build time rather than an
#: ImportError in a collector pod at three in the morning.
_Compression = Literal["uncompressed", "snappy", "gzip", "brotli", "lz4", "zstd"]


@dataclass(frozen=True)
class WriteReport:
    """What one write actually did, in the terms the Ledger records.

    ``rows_written`` counts the rows *this* call put into the lake, not the
    rows the partition ended up holding. The distinction is the whole value of
    the number: a re-collection of a session that was already complete writes
    the same count it wrote the first time, and a partition that grew by
    nothing would otherwise report zero and read as a failure.
    """

    rows_written: int = 0
    #: Relative to the lake root, so the value is portable between a test's
    #: temporary directory and the PVC - and so it can be recorded in a
    #: ``ingest_run.result`` blob that stays meaningful after a lake move.
    partitions: tuple[str, ...] = ()

    def merged_with(self, other: WriteReport) -> WriteReport:
        return WriteReport(
            rows_written=self.rows_written + other.rows_written,
            partitions=self.partitions + other.partitions,
        )


@dataclass
class LakeWriter:
    """Everything that puts bytes into the lake.

    Constructed per collection run and given that run's ``Provenance``, so
    every row it writes carries the same collection timestamp and the same
    build. A writer shared across runs would stamp rows with whichever run
    created it, which is the sort of provenance that is worse than none.
    """

    root: Path
    provenance: Provenance
    #: Compression. zstd rather than Parquet's default snappy: chain snapshots
    #: are the bulk of this lake and are mostly repeated strikes, expiries and
    #: near-constant greeks, which zstd at a low level compresses roughly twice
    #: as hard for a cost measured in milliseconds per file. It is a per-file
    #: property, so changing it later applies to new partitions and leaves old
    #: ones readable.
    compression: _Compression = "zstd"
    compression_level: int = 3
    _written: list[str] = field(default_factory=list[str], repr=False)

    def write_bars(self, bars: Sequence[Bar]) -> WriteReport:
        """Merge ``bars`` into whichever monthly partitions they belong to.

        Grouped here rather than by the caller because the grouping *is* the
        layout, and a collector that had to know which month a bar goes in
        would be the engine learning the directory structure by a different
        route.
        """
        if not bars:
            return WriteReport()

        grouped: dict[BarPartition, list[Bar]] = {}
        for bar in bars:
            key = BarPartition.containing(bar.interval, bar.symbol, bar.timestamp)
            grouped.setdefault(key, []).append(bar)

        report = WriteReport()
        for partition, rows in sorted(grouped.items(), key=lambda item: repr(item[0])):
            frame = bars_frame(rows, self.provenance)
            path = partition.path(self.root)
            self._merge_parquet(path, frame, BAR_KEY_COLUMNS, BAR_SCHEMA)
            report = report.merged_with(WriteReport(len(rows), (self._relative(path),)))
        return report

    def write_chain(self, snapshot: ChainSnapshot) -> WriteReport:
        """Write one chain snapshot to its own file.

        No merge: a snapshot is an observation of one instant and its file is
        named after that instant, so a re-run of the same snapshot produces the
        same path and replaces it wholesale. That is idempotency by
        construction rather than by key comparison, and it is the reason the
        chain layout is per-snapshot rather than per-day.

        An **empty** board is still written. A snapshot with no contracts is a
        real observation - an underlying whose board expired and has not been
        relisted - and the file saying so is the difference between that and a
        collection that did not run. The plan's rule about not recording
        silence as data is about *refusing to collect* when the market is
        closed, which happens one layer up in ``collect/chains.py``; by the
        time a snapshot exists, the market was open and this is what it held.
        """
        frame = chain_frame(snapshot, self.provenance)
        path = chain_partition(self.root, snapshot.underlying, snapshot.timestamp)
        self._write_parquet(path, frame)
        return WriteReport(frame.height, (self._relative(path),))

    # -- the two mechanisms -------------------------------------------------

    def _merge_parquet(
        self,
        path: Path,
        incoming: pl.DataFrame,
        key: Sequence[str],
        schema: dict[str, pl.DataType],
    ) -> None:
        """Replace the rows ``incoming`` names, keep everything else."""
        if path.is_file():
            existing = pl.read_parquet(path)
            # Cast rather than trust: a partition written by an older build
            # may predate a column, and `read_parquet` gives it whatever the
            # file says. Selecting the declared schema's columns in order also
            # means a column added to `schema.py` arrives as null on old rows
            # rather than as a concat error.
            existing = _conform(existing, schema)
            kept = existing.join(incoming.select(key), on=list(key), how="anti")
            merged = pl.concat([kept, incoming], how="vertical")
        else:
            merged = incoming
        self._write_parquet(path, merged.sort(list(key)))

    def _write_parquet(self, path: Path, frame: pl.DataFrame) -> None:
        """Write ``frame`` to ``path`` so that a kill leaves one whole file."""
        path.parent.mkdir(parents=True, exist_ok=True)

        # mkstemp rather than NamedTemporaryFile, because the file has to
        # outlive the handle: this writes it, closes it, and then renames it
        # onto the destination. The directory is the destination's own so that
        # `os.replace` is a rename within one filesystem, which is what makes
        # it atomic; a temporary file under /tmp would be a cross-device copy
        # with a window in the middle of it.
        descriptor, name = tempfile.mkstemp(
            dir=path.parent,
            prefix=f".{path.stem}.{os.getpid()}.",
            suffix=".parquet.tmp",
        )
        temporary = Path(name)
        try:
            with os.fdopen(descriptor, "wb") as handle:
                frame.write_parquet(
                    handle,
                    compression=self.compression,
                    compression_level=self.compression_level,
                )
                handle.flush()
                # The bytes are in the page cache after flush(); this is what
                # puts them on the device. Without it the rename below can be
                # durable while the contents it points at are not, which is
                # the one failure this whole dance exists to prevent - and it
                # is only visible after a node loses power, which is to say it
                # is only visible at the worst possible moment.
                os.fsync(handle.fileno())
            os.replace(temporary, path)
            _fsync_directory(path.parent)
        except BaseException:
            # BaseException, so a SIGINT during a long write cleans up too:
            # the plan's gate kills a collection mid-run, and a tree littered
            # with `.01.<pid>.parquet.tmp` files afterwards would be this
            # method's own mess rather than a property of the interruption.
            temporary.unlink(missing_ok=True)
            raise

    def _relative(self, path: Path) -> str:
        relative = path.relative_to(self.root).as_posix()
        self._written.append(relative)
        return relative


def _conform(frame: pl.DataFrame, schema: dict[str, pl.DataType]) -> pl.DataFrame:
    """``frame`` with exactly ``schema``'s columns, in order, at its dtypes.

    Missing columns arrive as nulls and unknown ones are dropped. Both are
    forward-compatibility rather than leniency: a column added to ``schema.py``
    has to be readable against partitions written before it existed, and a
    column removed from it should stop being carried forward on the next merge
    rather than pin the old shape indefinitely.
    """
    return frame.select(
        [
            pl.col(name).cast(dtype) if name in frame.columns else pl.lit(None, dtype).alias(name)
            for name, dtype in schema.items()
        ]
    )


def _fsync_directory(directory: Path) -> None:
    """Make the rename itself durable, where the platform allows it.

    ``os.replace`` updates a directory entry, and that entry is as cached as
    the file's contents were. Windows has no directory handle to sync and
    raises; that is not a supported deployment target for the lake (the PVC is
    ext4 on Longhorn), and a developer running the suite on one should get a
    passing test rather than a platform error, so the failure is swallowed
    rather than propagated.
    """
    try:
        fd = os.open(directory, os.O_RDONLY)
    except OSError:  # pragma: no cover - platform-dependent
        return
    try:
        os.fsync(fd)
    except OSError:  # pragma: no cover - platform-dependent
        logger.debug(
            "Could not fsync %s; the rename is durable only once the OS flushes", directory
        )
    finally:
        os.close(fd)
