"""The Ledger - the trading silo's Postgres, and the only database it has.

Named to keep it distinct from the *Lake* (Phase 3): Parquet on a PVC, read
in-process by DuckDB, holding market data and deliberately not in the CNPG
backup path. The split is argued in
[`docs/plans/trading.md`](../../../../docs/plans/trading.md) under "Why the
lake is not in Postgres" - one underlying's option chain at a five-minute
snapshot interval is ~78k rows a day, and fifty underlyings over a year clears
a billion. Putting that in CNPG would turn a trading workload into the
household's restore time.

What lands here instead is everything small and relational: what an instrument
is, where data came from, what ran and when, and - from Phase 5 - strategies,
parameter sets, runs and their metrics.
"""

from aerie_trading.db.engine import Database, SqlDatabase
from aerie_trading.db.models import Base, DataSource, IngestRun, Instrument

__all__ = [
    "Base",
    "DataSource",
    "Database",
    "IngestRun",
    "Instrument",
    "SqlDatabase",
]
