"""Provenance on the data source: where a number came from, in enough detail to make it again.

Phase 2 registers the synthetic generator as a ``data_source`` row "with the
seed and configuration recorded, so a run is reproducible from its provenance
alone" (docs/plans/trading.md). The Phase 1 table had nowhere to put that -
name, description and a flag - so the column arrives with the phase that has
something to write into it rather than with the phase that guessed at its
shape.

Nullable, and deliberately: the rows Phase 1 could have written have no
provenance to backfill, and inventing one for them would be a claim rather
than a record. ``base.chains_are_priceable_in`` reads a missing blob as "not
priceable", so the null case fails closed at the one place it matters.

Revision ID: 0002_data_source_config
Revises: 0001_ledger_floor
Create Date: 2026-09-02
"""

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision: str = "0002_data_source_config"
down_revision: str | None = "0001_ledger_floor"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column(
        "data_source",
        sa.Column("config", postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    )


def downgrade() -> None:
    op.drop_column("data_source", "config")
