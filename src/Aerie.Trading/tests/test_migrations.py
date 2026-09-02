"""That the migrations and the models describe the same schema.

This is the check `alembic revision --autogenerate` would otherwise be standing
in for, and it is worth having as a test rather than as a habit: the failure it
catches is a model edited without a migration, which does not break anything
until a deploy, and then breaks it as a query against a column that does not
exist.

It runs Alembic in **offline mode** - ``alembic upgrade head --sql`` - so it
needs no database and connects to nothing. What it compares is the DDL that
falls out the far end against the DDL SQLAlchemy renders from
``Base.metadata``. Both go through the same compiler for the same dialect, so
a difference in the output is a difference in the schema rather than a
difference in formatting.
"""

import io
import re
from contextlib import redirect_stdout
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy.dialects import postgresql
from sqlalchemy.schema import CreateIndex, CreateTable

from aerie_trading.db.models import Base

ROOT = Path(__file__).resolve().parent.parent


def normalise(statement: str) -> str:
    """One statement, whitespace collapsed and punctuation-trimmed."""
    return re.sub(r"\s+", " ", statement).strip().rstrip(";").strip()


def statements_from_migrations() -> list[str]:
    config = Config(str(ROOT / "alembic.ini"))
    config.set_main_option("script_location", str(ROOT / "aerie_trading" / "migrations"))

    buffer = io.StringIO()
    with redirect_stdout(buffer):
        command.upgrade(config, "head", sql=True)

    return schema_statements(buffer.getvalue())


def schema_statements(rendered: str) -> list[str]:
    """The CREATE statements out of an offline run, and nothing else.

    Offline mode writes more than SQL to stdout: BEGIN/COMMIT around the
    migration, Alembic's own progress lines, and the INSERT that stamps the
    applied revision into ``alembic_version``. None of those describe the
    schema, and the bookkeeping table is not part of it either.
    """
    statements: list[str] = []
    for chunk in rendered.split(";"):
        # Drop comment lines and the JSON log records env.py's own logging
        # emits onto the same stream.
        body = "\n".join(
            line for line in chunk.splitlines() if not line.lstrip().startswith(("--", "{"))
        )
        collapsed = normalise(body)
        if collapsed.startswith("CREATE ") and "alembic_version" not in collapsed:
            statements.append(collapsed)
    return statements


def statements_from_models() -> list[str]:
    dialect = postgresql.dialect()
    rendered: list[str] = []
    for table in Base.metadata.sorted_tables:
        rendered.append(normalise(str(CreateTable(table).compile(dialect=dialect))))
        for index in table.indexes:
            rendered.append(normalise(str(CreateIndex(index).compile(dialect=dialect))))
    return rendered


def test_the_migrations_build_exactly_the_schema_the_models_declare() -> None:
    from_migrations = statements_from_migrations()
    from_models = statements_from_models()

    # Sorted rather than in order: the order tables are created in is a
    # foreign-key constraint on the migration, not a property of the schema.
    assert sorted(from_migrations) == sorted(from_models)


def test_every_migration_can_be_undone() -> None:
    # A downgrade that was never written cannot be rehearsed, and the only free
    # time to rehearse one is while the database is empty. Offline mode renders
    # it without touching anything.
    config = Config(str(ROOT / "alembic.ini"))
    config.set_main_option("script_location", str(ROOT / "aerie_trading" / "migrations"))

    buffer = io.StringIO()
    with redirect_stdout(buffer):
        command.downgrade(config, "head:base", sql=True)

    rendered = buffer.getvalue()
    for table in Base.metadata.sorted_tables:
        assert f"DROP TABLE {table.name}" in rendered
