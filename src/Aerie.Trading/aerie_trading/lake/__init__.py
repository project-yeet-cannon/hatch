"""The Lake: Parquet on a volume, and the machinery that reads and writes it.

docs/plans/trading.md Phase 3, and *Why the lake is not in Postgres* for the
argument that put it here rather than in the Ledger.

Four modules, split on how expensive they are to import rather than on taste:

======================  ===================================================
``lake.layout``         where a row lives. Stdlib only.
``lake.schema``         what a row looks like. Needs polars.
``lake.writer``         idempotent writes. Needs polars.
``lake.reader``         DuckDB to polars. Needs both, and duckdb.
======================  ===================================================

**This package deliberately re-exports nothing**, for the reason
``providers/synthetic/__init__.py`` gives at length: ``Settings`` carries the
lake root, so every process in the silo can reach this package, including the
migration init container running under a 256 Mi limit
(``deploy/cluster/trading/app/deployment.yaml``). polars and duckdb are 90 MB
of wheel between them and neither belongs in a process whose entire job is
``alembic upgrade head``. Import the module you want::

    from aerie_trading.lake.layout import bar_partition
    from aerie_trading.lake.writer import LakeWriter
"""
