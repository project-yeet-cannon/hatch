"""What the control panel needs that the Ledger did not already hold.

docs/plans/trading.md Phase 7. Two changes, and both exist because a screen in
that phase would otherwise have to invent its content:

- **``run_curve``** - the run detail plots an equity curve, and until now the
  curve was the one thing ``run_backtest`` produced that nothing wrote down.
  Its own table rather than a column on ``run`` for the reason
  ``db/models.RunCurve`` gives at length: the leaderboard scans ``run``, and a
  payload on that row is one every leaderboard page carries and no leaderboard
  reads.
- **``sweep.seeded``** - the seed job *"must never touch a strategy, sweep or
  run the owner added"*, and the flag is what makes that a property of the row
  rather than of a naming convention an operator can collide with by calling
  their own sweep ``demo-ma-crossover``.

**Nothing is backfilled, and both directions are honest about it.** Every run
that existed before this migration ran without recording a curve, and there is
no arithmetic that recovers one from a blotter and a metric - so those runs
have no ``run_curve`` row, the run detail says the curve was not recorded, and
the alternative (a fabricated straight line between starting cash and final
equity) would be a chart that looks like a result. ``seeded`` defaults to
false, which is the true answer for every sweep enqueued before a seed job
existed.

Revision ID: 0006_the_control_panel
Revises: 0005_the_honesty_layer
Create Date: 2026-09-03
"""

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision: str = "0006_the_control_panel"
down_revision: str | None = "0005_the_honesty_layer"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column(
        "sweep",
        sa.Column("seeded", sa.Boolean(), server_default=sa.text("false"), nullable=False),
    )
    op.create_index(
        "ix_sweep_seeded_name",
        "sweep",
        ["name"],
        unique=False,
        postgresql_where=sa.text("seeded"),
    )

    op.create_table(
        "run_curve",
        sa.Column("run_id", sa.BigInteger(), nullable=False),
        sa.Column("points", postgresql.JSONB(astext_type=sa.Text()), nullable=False),
        sa.Column("points_total", sa.Integer(), nullable=False),
        sa.Column("sampled", sa.Boolean(), server_default=sa.text("false"), nullable=False),
        sa.CheckConstraint("points_total >= 0", name=op.f("ck_run_curve_points_total")),
        sa.ForeignKeyConstraint(
            ["run_id"],
            ["run.id"],
            name=op.f("fk_run_curve_run_id_run"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("run_id", name=op.f("pk_run_curve")),
    )


def downgrade() -> None:
    op.drop_table("run_curve")
    op.drop_index("ix_sweep_seeded_name", table_name="sweep", postgresql_where=sa.text("seeded"))
    op.drop_column("sweep", "seeded")
