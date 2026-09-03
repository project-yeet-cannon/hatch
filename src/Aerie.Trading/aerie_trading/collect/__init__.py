"""The collectors: everything that turns a provider into rows on the volume.

docs/plans/trading.md Phase 3. Two collectors, one recorder and a command-line
entry point, and the acceptance criterion for the whole package is written into
that phase: **none of it may know which provider it is talking to.** If any of
this needs changing when Schwab arrives, the interface in ``providers/base.py``
was drawn in the wrong place and the fix belongs there rather than here.

====================  ===================================================
``collect.config``    the watchlists and intervals, as values.
``collect.runs``      one ``ingest_run`` row per collection, in the Ledger.
``collect.bars``      daily and intraday bars: backfill and incremental.
``collect.chains``    the flagship - a board snapshotted through a session.
``collect.__main__``  ``python -m aerie_trading.collect``, what a CronJob runs.
====================  ===================================================

**This package re-exports nothing**, the same discipline
``providers/synthetic/__init__.py`` and ``lake/__init__.py`` keep and for the
same reason: ``Settings`` carries a ``CollectionConfig``, so every process in
the silo imports ``.config``, and ``.config`` must not drag polars, duckdb or
pandas into the migration init container behind it.
"""
