"""Recording which source produced which data, in the Ledger.

docs/plans/trading.md Phase 2: the provider is "registered as a ``data_source``
row, with the seed and configuration recorded, so a run is reproducible from
its provenance alone." That last clause is the requirement doing the work.
Phase 5 records the ``aerie-revision`` that produced a run and Phase 6 records
its windows; without the source's own configuration beside them, a run from
March is reproducible only if nobody re-seeded the generator in between, which
is a thing nobody would remember either way.

It is also where the plan's chain guardrail survives the trip through the lake.
A backtest reads rows, not providers, and all that reaches it of the provider
is a ``data_source`` row - so the ``chains_are_priceable`` claim is written into
the provenance blob here, and ``base.chains_are_priceable_in`` reads it back
out, failing closed when it is absent.
"""

from __future__ import annotations

from sqlalchemy import select
from sqlalchemy.orm import Session

from aerie_trading.db.models import DataSource
from aerie_trading.providers.base import MarketDataProvider

__all__ = ["ensure_data_source"]


def ensure_data_source(session: Session, provider: MarketDataProvider) -> DataSource:
    """Insert or update ``provider``'s row, and return it.

    Idempotent by name, because every process that touches a provider calls
    this at startup - a collector, a worker, a backfill - and the second one
    to arrive must not fail on a unique constraint or, worse, insert a second
    row that half the data then points at.

    **The config is overwritten, not merged, and not left alone.** A row whose
    provenance describes an older configuration is worse than no provenance:
    it is a record that is confidently wrong about what produced the rows
    written after it. Overwriting means "this is the configuration in force
    now", which is the only claim this table can honestly make; the history of
    what was in force *then* belongs to ``ingest_run``, which records one row
    per call and is written by Phase 3.

    Does not commit. The caller owns the transaction, because registration is
    the first thing a collection run does and it belongs in the same
    transaction as the run it is registering for.
    """
    provenance = dict(provider.provenance)
    existing = session.scalar(select(DataSource).where(DataSource.name == provider.name))

    if existing is None:
        created = DataSource(
            name=provider.name,
            description=provider.description,
            config=provenance,
        )
        session.add(created)
        return created

    # Assigned unconditionally rather than behind an equality check: SQLAlchemy
    # already suppresses an UPDATE for an unchanged attribute, so a guard here
    # would only add a branch that has to be right about dict comparison.
    existing.description = provider.description
    existing.config = provenance
    return existing
