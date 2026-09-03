"""The honesty layer's half of the schema.

docs/plans/trading.md Phase 6. Four changes, and each is one bullet of that
phase made structural rather than conventional:

- **``run.oos_start`` / ``run.oos_end``** - *"Every run declares in-sample and
  out-of-sample windows in its record. A run with no out-of-sample window is a
  valid object that can never be a headline number."* Both null is that valid
  object; the refusal lives in ``honesty/presentation.py``, which reads these
  two columns and nothing else.
- **``run.kind``**, and ``param_set_id`` becoming nullable under a CHECK that
  is stronger than the NOT NULL it replaces. The walk-forward evaluation of a
  sweep is a work item on the same queue as everything else, and it has no
  single parameter set because choosing one per fold is what it does.
- **``sweep.trials``** - *"a run knows how many siblings its sweep produced"*.
  Distinct from ``total_runs``, which now also counts the walk-forward row.
- **``walk_forward_fold``** - the sequence of choices a walk-forward made,
  which is the only durable record of what it actually did.

**Backfilling ``sweep.trials`` from ``total_runs``** is correct rather than
approximate on this schema: every sweep written before this migration held one
run per parameter set and nothing else, so the two numbers were equal by
construction. The server default of 0 covers a row inserted by a build that
predates the column, which cannot exist, and is there because a NOT NULL column
added to a live table needs one.

Revision ID: 0005_the_honesty_layer
Revises: 0004_runs_and_the_queue
Create Date: 2026-09-03
"""

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = "0005_the_honesty_layer"
down_revision: str | None = "0004_runs_and_the_queue"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column(
        "sweep",
        sa.Column("trials", sa.Integer(), server_default=sa.text("0"), nullable=False),
    )
    # See the module docstring: equal by construction on every row that can
    # exist at this point, so this is a backfill rather than an estimate.
    op.execute("UPDATE sweep SET trials = total_runs")
    op.create_check_constraint("trials", "sweep", "trials >= 0")

    op.add_column(
        "run",
        sa.Column(
            "kind", sa.String(length=16), server_default=sa.text("'backtest'"), nullable=False
        ),
    )
    op.add_column("run", sa.Column("oos_start", sa.DateTime(timezone=True), nullable=True))
    op.add_column("run", sa.Column("oos_end", sa.DateTime(timezone=True), nullable=True))
    op.alter_column("run", "param_set_id", existing_type=sa.BigInteger(), nullable=True)

    op.create_check_constraint("kind", "run", "kind IN ('backtest', 'walk_forward')")
    op.create_check_constraint(
        "param_set_by_kind", "run", "(kind = 'walk_forward') = (param_set_id IS NULL)"
    )
    op.create_check_constraint(
        "walk_forward_has_a_sweep",
        "run",
        "kind <> 'walk_forward' OR sweep_id IS NOT NULL",
    )
    op.create_check_constraint("oos_window", "run", "(oos_start IS NULL) = (oos_end IS NULL)")
    op.create_check_constraint(
        "oos_within_window",
        "run",
        "oos_start IS NULL OR ("
        " oos_start >= window_start AND oos_end <= window_end AND oos_end > oos_start)",
    )

    op.create_table(
        "walk_forward_fold",
        sa.Column("run_id", sa.BigInteger(), nullable=False),
        sa.Column("fold", sa.Integer(), nullable=False),
        sa.Column("train_start", sa.DateTime(timezone=True), nullable=False),
        sa.Column("train_end", sa.DateTime(timezone=True), nullable=False),
        sa.Column("test_start", sa.DateTime(timezone=True), nullable=False),
        sa.Column("test_end", sa.DateTime(timezone=True), nullable=False),
        sa.Column("param_set_id", sa.BigInteger(), nullable=False),
        sa.Column("candidates", sa.Integer(), nullable=False),
        sa.Column("train_objective", sa.Numeric(), nullable=False),
        sa.Column("starting_cash", sa.Numeric(), nullable=False),
        sa.Column("ending_equity", sa.Numeric(), nullable=False),
        sa.CheckConstraint("fold >= 0", name=op.f("ck_walk_forward_fold_fold")),
        sa.CheckConstraint("candidates > 0", name=op.f("ck_walk_forward_fold_candidates")),
        sa.CheckConstraint(
            "train_end > train_start", name=op.f("ck_walk_forward_fold_train_window")
        ),
        sa.CheckConstraint("test_end > test_start", name=op.f("ck_walk_forward_fold_test_window")),
        sa.CheckConstraint(
            "test_start >= train_end", name=op.f("ck_walk_forward_fold_train_precedes_test")
        ),
        sa.ForeignKeyConstraint(
            ["param_set_id"],
            ["param_set.id"],
            name=op.f("fk_walk_forward_fold_param_set_id_param_set"),
            ondelete="RESTRICT",
        ),
        sa.ForeignKeyConstraint(
            ["run_id"],
            ["run.id"],
            name=op.f("fk_walk_forward_fold_run_id_run"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("run_id", "fold", name=op.f("pk_walk_forward_fold")),
    )


def downgrade() -> None:
    op.drop_table("walk_forward_fold")

    op.drop_constraint("oos_within_window", "run", type_="check")
    op.drop_constraint("oos_window", "run", type_="check")
    op.drop_constraint("walk_forward_has_a_sweep", "run", type_="check")
    op.drop_constraint("param_set_by_kind", "run", type_="check")
    op.drop_constraint("kind", "run", type_="check")

    # The walk-forward rows go before the column that identifies them does.
    # Restoring NOT NULL on param_set_id would otherwise fail on exactly the
    # rows this migration made possible, which is the downgrade failing on the
    # only database that needs it.
    op.execute("DELETE FROM run WHERE kind = 'walk_forward'")
    op.alter_column("run", "param_set_id", existing_type=sa.BigInteger(), nullable=False)
    op.drop_column("run", "oos_end")
    op.drop_column("run", "oos_start")
    op.drop_column("run", "kind")

    op.drop_constraint("trials", "sweep", type_="check")
    op.drop_column("sweep", "trials")
