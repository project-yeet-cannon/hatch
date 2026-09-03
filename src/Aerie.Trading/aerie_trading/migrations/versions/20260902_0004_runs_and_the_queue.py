"""Runs, sweeps, and the queue they are drawn from.

docs/plans/trading.md Phase 5: *"Tables: strategy, param_set, run, trade,
run_metric. A run records its strategy, its parameters, its data window, the
aerie-revision that produced it, and the lake state it read."* Six tables
rather than five - ``sweep`` is the addition, and ``db/models.py`` argues for it
where it is defined rather than here.

**The scheduling columns are on ``run`` rather than in a queue table of their
own.** The phase asks for a Postgres work queue whose *"queue depth becomes
rows the control panel already reads"*, and a second table beside this one
would make depth a join and would admit the two states every split queue
eventually reaches: a job whose run vanished, and a run with two jobs. The
whole queue is therefore ``status``, ``attempts``, ``available_at`` and a lease,
indexed partially so that the claim query reads an index over the queued
minority rather than over a table that grows forever.

Two indexes are partial and one is unique, and none of the three is an
optimisation added on a guess:

- ``ix_run_claimable`` is what ``SELECT ... FOR UPDATE SKIP LOCKED`` walks.
  Partial on ``status = 'queued'`` because finished runs are permanent and
  queued ones are transient; a full index would be almost entirely entries no
  claim will ever read.
- ``ix_run_leased`` is the reaper's, over the runs currently held.
- ``uq_trade_run_sequence`` is the backstop under the lease fence: if the fence
  is ever wrong, a second blotter for one run is a constraint violation rather
  than a doubled P&L.

Revision ID: 0004_runs_and_the_queue
Revises: 0003_ingest_run_result
Create Date: 2026-09-02
"""

from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op
from sqlalchemy.dialects import postgresql

revision: str = "0004_runs_and_the_queue"
down_revision: str | None = "0003_ingest_run_result"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "strategy",
        sa.Column("id", sa.BigInteger(), autoincrement=True, nullable=False),
        sa.Column("name", sa.String(length=64), nullable=False),
        sa.Column("description", sa.Text(), nullable=False),
        sa.Column("params_schema", postgresql.JSONB(astext_type=sa.Text()), nullable=False),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            server_default=sa.text("now()"),
            nullable=False,
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_strategy")),
        sa.UniqueConstraint("name", name=op.f("uq_strategy_name")),
    )

    op.create_table(
        "param_set",
        sa.Column("id", sa.BigInteger(), autoincrement=True, nullable=False),
        sa.Column("strategy_id", sa.BigInteger(), nullable=False),
        sa.Column("params", postgresql.JSONB(astext_type=sa.Text()), nullable=False),
        sa.Column("params_hash", sa.String(length=64), nullable=False),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            server_default=sa.text("now()"),
            nullable=False,
        ),
        sa.ForeignKeyConstraint(
            ["strategy_id"],
            ["strategy.id"],
            name=op.f("fk_param_set_strategy_id_strategy"),
            ondelete="RESTRICT",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_param_set")),
    )
    op.create_index(
        "uq_param_set_strategy_hash", "param_set", ["strategy_id", "params_hash"], unique=True
    )

    op.create_table(
        "sweep",
        sa.Column("id", sa.BigInteger(), autoincrement=True, nullable=False),
        sa.Column("name", sa.String(length=128), nullable=False),
        sa.Column("strategy_id", sa.BigInteger(), nullable=False),
        sa.Column("spec", postgresql.JSONB(astext_type=sa.Text()), nullable=False),
        sa.Column("total_runs", sa.Integer(), nullable=False),
        sa.Column("aerie_revision", sa.String(length=40), nullable=True),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            server_default=sa.text("now()"),
            nullable=False,
        ),
        sa.Column("cancelled_at", sa.DateTime(timezone=True), nullable=True),
        sa.CheckConstraint("total_runs >= 0", name=op.f("ck_sweep_total_runs")),
        sa.ForeignKeyConstraint(
            ["strategy_id"],
            ["strategy.id"],
            name=op.f("fk_sweep_strategy_id_strategy"),
            ondelete="RESTRICT",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_sweep")),
    )

    op.create_table(
        "run",
        sa.Column("id", sa.BigInteger(), autoincrement=True, nullable=False),
        sa.Column("sweep_id", sa.BigInteger(), nullable=True),
        sa.Column("strategy_id", sa.BigInteger(), nullable=False),
        sa.Column("param_set_id", sa.BigInteger(), nullable=False),
        sa.Column("data_source_id", sa.BigInteger(), nullable=False),
        sa.Column("symbols", postgresql.ARRAY(sa.Text()), nullable=False),
        sa.Column("interval", sa.String(length=8), nullable=False),
        sa.Column("window_start", sa.DateTime(timezone=True), nullable=False),
        sa.Column("window_end", sa.DateTime(timezone=True), nullable=False),
        sa.Column("starting_cash", sa.Numeric(precision=18, scale=2), nullable=False),
        sa.Column("costs", postgresql.JSONB(astext_type=sa.Text()), nullable=False),
        sa.Column("status", sa.String(length=16), nullable=False),
        sa.Column("priority", sa.Integer(), server_default=sa.text("100"), nullable=False),
        sa.Column("attempts", sa.Integer(), server_default=sa.text("0"), nullable=False),
        sa.Column("max_attempts", sa.Integer(), server_default=sa.text("3"), nullable=False),
        sa.Column(
            "available_at",
            sa.DateTime(timezone=True),
            server_default=sa.text("now()"),
            nullable=False,
        ),
        sa.Column("lease_token", sa.Uuid(), nullable=True),
        sa.Column("lease_expires_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("leased_by", sa.String(length=128), nullable=True),
        sa.Column(
            "enqueued_at",
            sa.DateTime(timezone=True),
            server_default=sa.text("now()"),
            nullable=False,
        ),
        sa.Column("started_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("finished_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("duration_ms", sa.Integer(), nullable=True),
        sa.Column("bars", sa.Integer(), nullable=True),
        sa.Column("aerie_revision", sa.String(length=40), nullable=True),
        sa.Column("data_fingerprint", sa.String(length=64), nullable=True),
        sa.Column("result_fingerprint", sa.String(length=64), nullable=True),
        sa.Column("error", sa.Text(), nullable=True),
        sa.CheckConstraint("attempts >= 0", name=op.f("ck_run_attempts")),
        sa.CheckConstraint("cardinality(symbols) > 0", name=op.f("ck_run_symbols")),
        sa.CheckConstraint("max_attempts > 0", name=op.f("ck_run_max_attempts")),
        sa.CheckConstraint("starting_cash > 0", name=op.f("ck_run_starting_cash")),
        sa.CheckConstraint(
            "status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')",
            name=op.f("ck_run_status"),
        ),
        sa.CheckConstraint("window_end > window_start", name=op.f("ck_run_window")),
        sa.ForeignKeyConstraint(
            ["data_source_id"],
            ["data_source.id"],
            name=op.f("fk_run_data_source_id_data_source"),
            ondelete="RESTRICT",
        ),
        sa.ForeignKeyConstraint(
            ["param_set_id"],
            ["param_set.id"],
            name=op.f("fk_run_param_set_id_param_set"),
            ondelete="RESTRICT",
        ),
        sa.ForeignKeyConstraint(
            ["strategy_id"],
            ["strategy.id"],
            name=op.f("fk_run_strategy_id_strategy"),
            ondelete="RESTRICT",
        ),
        sa.ForeignKeyConstraint(
            ["sweep_id"],
            ["sweep.id"],
            name=op.f("fk_run_sweep_id_sweep"),
            ondelete="RESTRICT",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_run")),
    )
    op.create_index(
        "ix_run_claimable",
        "run",
        ["priority", "available_at", "id"],
        postgresql_where=sa.text("status = 'queued'"),
    )
    op.create_index(
        "ix_run_leased",
        "run",
        ["lease_expires_at"],
        postgresql_where=sa.text("status = 'running'"),
    )
    op.create_index("ix_run_sweep_status", "run", ["sweep_id", "status"])

    op.create_table(
        "trade",
        sa.Column("id", sa.BigInteger(), autoincrement=True, nullable=False),
        sa.Column("run_id", sa.BigInteger(), nullable=False),
        sa.Column("sequence", sa.Integer(), nullable=False),
        sa.Column("instrument_id", sa.BigInteger(), nullable=False),
        sa.Column("filled_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("quantity", sa.Integer(), nullable=False),
        sa.Column("price", sa.Numeric(), nullable=False),
        sa.Column("reference_price", sa.Numeric(), nullable=False),
        sa.Column("commission", sa.Numeric(), nullable=False),
        sa.Column("realized_pnl", sa.Numeric(), nullable=False),
        sa.Column("position_key", sa.String(length=64), nullable=False),
        sa.Column("tag", sa.String(length=64), server_default=sa.text("''"), nullable=False),
        sa.CheckConstraint("quantity <> 0", name=op.f("ck_trade_quantity")),
        sa.CheckConstraint("sequence >= 0", name=op.f("ck_trade_sequence")),
        sa.ForeignKeyConstraint(
            ["instrument_id"],
            ["instrument.id"],
            name=op.f("fk_trade_instrument_id_instrument"),
            ondelete="RESTRICT",
        ),
        sa.ForeignKeyConstraint(
            ["run_id"],
            ["run.id"],
            name=op.f("fk_trade_run_id_run"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("id", name=op.f("pk_trade")),
    )
    op.create_index("uq_trade_run_sequence", "trade", ["run_id", "sequence"], unique=True)

    op.create_table(
        "run_metric",
        sa.Column("run_id", sa.BigInteger(), nullable=False),
        sa.Column("name", sa.String(length=48), nullable=False),
        sa.Column("value", sa.Numeric(), nullable=False),
        sa.ForeignKeyConstraint(
            ["run_id"],
            ["run.id"],
            name=op.f("fk_run_metric_run_id_run"),
            ondelete="CASCADE",
        ),
        sa.PrimaryKeyConstraint("run_id", "name", name=op.f("pk_run_metric")),
    )


def downgrade() -> None:
    op.drop_table("run_metric")
    op.drop_index("uq_trade_run_sequence", table_name="trade")
    op.drop_table("trade")
    op.drop_index("ix_run_sweep_status", table_name="run")
    op.drop_index("ix_run_leased", table_name="run")
    op.drop_index("ix_run_claimable", table_name="run")
    op.drop_table("run")
    op.drop_table("sweep")
    op.drop_index("uq_param_set_strategy_hash", table_name="param_set")
    op.drop_table("param_set")
    op.drop_table("strategy")
