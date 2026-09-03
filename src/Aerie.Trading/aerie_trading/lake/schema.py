"""What a row in the lake looks like, and how a provider record becomes one.

The schemas are declared rather than inferred, and that is the whole point of
the module. polars will happily infer a frame's dtypes from the values it is
handed, which means a month whose every option had zero open interest gets an
``Int64`` column in one file and a month that had some gets ``Int64`` in the
other only by luck - and the day an inference differs, DuckDB reads the two
files as incompatible and reports it as a schema error on a query nobody
changed. A declared schema makes a partition's shape a property of this file
rather than of the data that happened to arrive.

**Three columns are on every row and are not market data**: ``provider``,
``collected_at`` and ``aerie_revision``. docs/plans/trading.md Phase 3 asks for
them per *file*; they are stored per *row* instead, which is a deliberate
widening. Parquet dictionary-encodes a column holding one repeated value down
to almost nothing, so the cost is negligible - and the benefit is that they
survive the read. A file-level key/value carries no further than the file: the
moment the reader concatenates twelve partitions into one frame, a caller
asking "which build wrote this row, and when" would have to go back to the
directory structure, which is the one thing ``reader.py`` exists to prevent.
It is also what makes the provenance join in Phase 6 a column expression
rather than a filesystem walk.

Two spellings worth stating:

- ``option_right``, never ``right``. ``db/models.py`` renamed the column for
  SQL's sake (RIGHT opens a RIGHT JOIN) and DuckDB has the same reserved word
  for the same reason, so the lake and the Ledger use one name and neither
  needs quoting in a hand-written query during an incident.
- **Microsecond timestamps, UTC, everywhere.** ``us`` rather than polars'
  default nanoseconds because Parquet's INT96 legacy and DuckDB's own
  ``TIMESTAMP`` are both microsecond, so nanoseconds would be a precision this
  format converts away on every round trip while nothing here can produce one.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from datetime import UTC, datetime
from typing import Final

import polars as pl

from aerie_trading.providers.base import Bar, ChainSnapshot, Interval

__all__ = [
    "BAR_KEY_COLUMNS",
    "BAR_SCHEMA",
    "CHAIN_KEY_COLUMNS",
    "CHAIN_SCHEMA",
    "PROVENANCE_COLUMNS",
    "Provenance",
    "bars_frame",
    "chain_frame",
    "empty_frame",
]

#: A UTC instant at microsecond resolution. Bound once because it is the dtype
#: of five columns across two schemas and a disagreement between any two of
#: them is a read error rather than a wrong number - which is the good failure,
#: but only if it cannot happen at all.
_UTC_STAMP: Final = pl.Datetime("us", "UTC")

#: The three columns that describe the collection rather than the market. See
#: the module docstring for why they are columns rather than file metadata.
PROVENANCE_COLUMNS: Final[tuple[str, ...]] = ("provider", "collected_at", "aerie_revision")

_PROVENANCE_SCHEMA: Final[dict[str, pl.DataType]] = {
    "provider": pl.String(),
    "collected_at": _UTC_STAMP,
    # 40 hex characters, or `dev` for an unstamped build - see
    # aerie_trading/revision.py. Stored as text rather than as a fixed-width
    # binary because the value an operator compares it against is the sha they
    # read off a commit.
    "aerie_revision": pl.String(),
}

#: ``bars/{interval}/{symbol}/{year}/{month}.parquet``.
#:
#: ``adjusted_close`` sits beside ``close`` because the plan's layout stores
#: both. For a source with no corporate actions the two are equal, which is a
#: fact about that source rather than a reason to drop the column: the day a
#: provider reports a split, a backtest that had been reading ``close`` is
#: wrong across the split and nothing tells it so.
BAR_SCHEMA: Final[dict[str, pl.DataType]] = {
    "symbol": pl.String(),
    "interval": pl.String(),
    "timestamp": _UTC_STAMP,
    "open": pl.Float64(),
    "high": pl.Float64(),
    "low": pl.Float64(),
    "close": pl.Float64(),
    "adjusted_close": pl.Float64(),
    "volume": pl.Int64(),
    **_PROVENANCE_SCHEMA,
}

#: ``chains/{underlying}/{date}/{hhmm}.parquet`` - one row per contract per
#: snapshot, which is the plan's flagship dataset and the one that cannot be
#: re-collected.
#:
#: ``spot`` is repeated on every row of a snapshot and is not redundant: it is
#: the underlying's price *at the instant of the snapshot*, which is a
#: different measurement from the underlying's minute bar and is unrecoverable
#: afterwards. Every moneyness calculation anyone ever does with this file is
#: exactly as correct as that column.
CHAIN_SCHEMA: Final[dict[str, pl.DataType]] = {
    "underlying": pl.String(),
    "symbol": pl.String(),
    "expiry": pl.Date(),
    "strike": pl.Float64(),
    "option_right": pl.String(),
    "timestamp": _UTC_STAMP,
    "spot": pl.Float64(),
    "bid": pl.Float64(),
    "ask": pl.Float64(),
    "last": pl.Float64(),
    "volume": pl.Int64(),
    "open_interest": pl.Int64(),
    "implied_volatility": pl.Float64(),
    "delta": pl.Float64(),
    "gamma": pl.Float64(),
    "theta": pl.Float64(),
    "vega": pl.Float64(),
    "rho": pl.Float64(),
    "multiplier": pl.Int32(),
    **_PROVENANCE_SCHEMA,
}

#: What identifies a bar within its partition, for the merge that makes a
#: re-run idempotent (``writer.py``). ``timestamp`` alone would do while a
#: partition is one symbol at one interval, and all three are named anyway:
#: the day the partition granularity changes, the merge should still be
#: correct rather than silently keying on too little.
BAR_KEY_COLUMNS: Final[tuple[str, ...]] = ("symbol", "interval", "timestamp")

#: The same, for chains. A snapshot is a whole file that is written once and
#: replaced wholesale, so this is not used by the writer - it is what a caller
#: joining two snapshots together needs, and it is declared beside the schema
#: it belongs to rather than rediscovered at each call site.
CHAIN_KEY_COLUMNS: Final[tuple[str, ...]] = ("underlying", "symbol", "timestamp")


class Provenance:
    """Who collected these rows, when, and from which build.

    Constructed once per collection run and stamped onto every row it writes,
    so a partition rewritten by a later run carries that run's identity and not
    a mixture. That matters for the re-run case specifically: the merge in
    ``writer.py`` keeps existing rows the new frame does not name, so a
    partition can legitimately hold rows from two builds - and the only way to
    tell which is which afterwards is that the columns are per row.
    """

    __slots__ = ("collected_at", "provider", "revision")

    def __init__(self, provider: str, revision: str, collected_at: datetime | None = None) -> None:
        moment = collected_at if collected_at is not None else datetime.now(UTC)
        if moment.tzinfo is None:
            raise ValueError("collected_at must be timezone-aware")
        self.provider = provider
        self.revision = revision
        self.collected_at = moment.astimezone(UTC)

    def columns(self) -> Mapping[str, object]:
        return {
            "provider": self.provider,
            "collected_at": self.collected_at,
            "aerie_revision": self.revision,
        }


def empty_frame(schema: Mapping[str, pl.DataType]) -> pl.DataFrame:
    """A frame with the right columns and no rows.

    The answer to a read that found no partitions, and the reason it is a
    function rather than a constant: a caller that mutated a shared empty frame
    would be mutating every future empty answer.
    """
    return pl.DataFrame(schema=dict(schema))


def bars_frame(bars: Sequence[Bar], provenance: Provenance) -> pl.DataFrame:
    """Provider bars as a frame in ``BAR_SCHEMA``.

    Sorted by the key columns on the way out, so that a partition on disk is
    always in timestamp order regardless of what order the provider answered
    in. A sorted Parquet file is not just tidy - it is what lets a range scan
    skip row groups by their statistics, which is most of why reading a decade
    of one symbol is cheap.
    """
    stamp = provenance.columns()
    rows = [
        {
            "symbol": bar.symbol,
            "interval": bar.interval.value,
            "timestamp": bar.timestamp,
            "open": bar.open,
            "high": bar.high,
            "low": bar.low,
            "close": bar.close,
            "adjusted_close": bar.adjusted_close,
            "volume": bar.volume,
            **stamp,
        }
        for bar in bars
    ]
    frame = pl.DataFrame(rows, schema=dict(BAR_SCHEMA), orient="row" if rows else None)
    return frame.sort(BAR_KEY_COLUMNS)


def chain_frame(snapshot: ChainSnapshot, provenance: Provenance) -> pl.DataFrame:
    """One ``ChainSnapshot`` as a frame in ``CHAIN_SCHEMA``.

    The snapshot's ``spot`` and ``timestamp`` are broadcast onto every contract
    rather than taken from the contracts themselves: a provider is free to
    report each quote with its own stamp, and the snapshot's instant is the one
    the file is named after. Two spellings of "when" in one file is a thing to
    argue about later; one is not.
    """
    stamp = provenance.columns()
    rows = [
        {
            "underlying": snapshot.underlying,
            "symbol": contract.symbol,
            "expiry": contract.expiry,
            "strike": contract.strike,
            "option_right": contract.right.value,
            "timestamp": snapshot.timestamp,
            "spot": snapshot.spot,
            "bid": contract.bid,
            "ask": contract.ask,
            "last": contract.last,
            "volume": contract.volume,
            "open_interest": contract.open_interest,
            "implied_volatility": contract.implied_volatility,
            "delta": contract.delta,
            "gamma": contract.gamma,
            "theta": contract.theta,
            "vega": contract.vega,
            "rho": contract.rho,
            "multiplier": contract.multiplier,
            **stamp,
        }
        for contract in snapshot.contracts
    ]
    frame = pl.DataFrame(rows, schema=dict(CHAIN_SCHEMA), orient="row" if rows else None)
    # Strike then right then expiry is the order a person reads a board in, and
    # the order a moneyness scan wants; sorting here means no reader has to.
    return frame.sort(("expiry", "option_right", "strike"))


def interval_of(frame: pl.DataFrame) -> Interval | None:
    """The single interval a bar frame holds, or ``None`` if it holds none.

    Raises when a frame holds more than one, which is a caller error rather
    than a data error: every read path in ``reader.py`` is per-interval, so a
    mixed frame means two of them were concatenated by hand.
    """
    if frame.is_empty():
        return None
    values = frame.get_column("interval").unique().to_list()
    if len(values) != 1:
        raise ValueError(f"frame holds {len(values)} intervals; expected exactly one")
    return Interval(values[0])
