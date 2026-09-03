"""What a collection run produced, and what it could not find.

docs/plans/trading.md Phase 3 gates on collection health being visible: "rows
written, gaps detected, last successful run per collector". The first and third
of those were already columns on ``ingest_run``; the second had nowhere to go,
and neither did the detail an operator needs when it fires - which partitions a
run wrote, and which sessions it came back empty for.

Two columns, and the split between them is deliberate. ``gap_count`` is the one
fact that is *aggregated* - the collection-health gauge is a MAX over it per
collector - so it is a plain integer a query can index and sum rather than a
``jsonb_array_length`` over a blob. ``result`` is everything a person reads one
row at a time, and it is the counterpart to the ``request`` column Phase 1
built: that one says what was asked for, this one says what came back.

Both nullable, and for ``gap_count`` the null case carries meaning: a run that
failed before it could compare what it asked for against what it got has no
honest gap count, and writing 0 would put a clean number on a run that never
checked.

Revision ID: 0003_ingest_run_result
Revises: 0002_data_source_config
Create Date: 2026-09-02
"""

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision: str = "0003_ingest_run_result"
down_revision: str | None = "0002_data_source_config"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column("ingest_run", sa.Column("gap_count", sa.Integer(), nullable=True))
    op.add_column(
        "ingest_run",
        sa.Column("result", postgresql.JSONB(astext_type=sa.Text()), nullable=True),
    )
    # `gap_count`, not `ck_ingest_run_gap_count`. Alembic runs the name given
    # here through `Base.metadata`'s naming convention (env.py hands it the
    # same MetaData the models use), so passing the finished name produces
    # `ck_ingest_run_ck_ingest_run_gap_count` - which applies cleanly, is
    # wrong, and differs from the models by a string nobody would read.
    # tests/test_migrations.py caught exactly that on this migration's first
    # run, which is the whole reason that test compares constraints and not
    # just columns.
    op.create_check_constraint(
        "gap_count",
        "ingest_run",
        "gap_count IS NULL OR gap_count >= 0",
    )


def downgrade() -> None:
    op.drop_constraint("gap_count", "ingest_run", type_="check")
    op.drop_column("ingest_run", "result")
    op.drop_column("ingest_run", "gap_count")
