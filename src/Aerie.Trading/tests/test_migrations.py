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

**Comparing the statements as text stopped working at the second migration**,
and the failure was silent in the dangerous direction: an ``ALTER TABLE ...
ADD COLUMN`` is not a ``CREATE``, so the first version of this file discarded
it and then found the two schemas equal because neither mentioned the new
column. So the statements are reduced to a *schema* first - a table's set of
column and constraint definitions, with the ALTERs applied - and the schemas
are compared. Sets rather than sequences, because ``ADD COLUMN`` appends and a
``CREATE TABLE`` from the models declares the column wherever the class does;
column order is the one difference between these two paths that is not a
difference in schema.

Anything the reducer does not recognise raises rather than being skipped. That
is the whole lesson of the first version: a check that quietly ignores the DDL
it was not taught about passes for the wrong reason, and the next person to add
an ``ALTER COLUMN`` should be told to extend this file rather than be reassured
by it.
"""

import io
import re
from contextlib import redirect_stdout
from pathlib import Path

import pytest
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
    """The DDL out of an offline run, and nothing else.

    Offline mode writes more than DDL to stdout: BEGIN/COMMIT around the
    migration, Alembic's own progress lines, and the INSERT and UPDATE that
    stamp the applied revision into ``alembic_version``. None of those describe
    the schema, and the bookkeeping table is not part of it either.
    """
    statements: list[str] = []
    for chunk in rendered.split(";"):
        # Drop comment lines and the JSON log records env.py's own logging
        # emits onto the same stream.
        body = "\n".join(
            line for line in chunk.splitlines() if not line.lstrip().startswith(("--", "{"))
        )
        collapsed = normalise(body)
        if collapsed.startswith(("CREATE ", "ALTER ")) and "alembic_version" not in collapsed:
            statements.append(collapsed)
    return statements


#: A table, as a set of the definitions inside its parentheses - one per column
#: and one per table-level constraint - plus the indexes declared on it
#: separately. Two of these are equal when the schemas are.
Schema = dict[str, set[str]]

_INDEXES = "\x00indexes"


def _split_definitions(body: str) -> list[str]:
    """Split a ``CREATE TABLE`` body on the commas that separate definitions.

    Depth-aware, because ``NUMERIC(12, 4)`` and every multi-column constraint
    contain commas that separate nothing.
    """
    definitions: list[str] = []
    depth = 0
    current: list[str] = []
    for character in body:
        if character == "," and depth == 0:
            definitions.append("".join(current).strip())
            current = []
            continue
        if character == "(":
            depth += 1
        elif character == ")":
            depth -= 1
        current.append(character)
    if "".join(current).strip():
        definitions.append("".join(current).strip())
    return definitions


def _apply_alter(schema: Schema, statement: str) -> None:
    """Fold one ``ALTER TABLE`` into the schema built so far."""
    remainder = statement[len("ALTER TABLE ") :]
    table, _, action = remainder.partition(" ")
    if table not in schema:
        raise AssertionError(f"{statement!r} alters a table this run never created")
    definitions = schema[table]

    if action.startswith("ADD COLUMN "):
        definitions.add(action[len("ADD COLUMN ") :])
    elif action.startswith("DROP COLUMN "):
        column = action[len("DROP COLUMN ") :]
        definitions.difference_update(
            {definition for definition in definitions if definition.split(" ")[0] == column}
        )
    elif action.startswith("ADD CONSTRAINT "):
        definitions.add("CONSTRAINT " + action[len("ADD CONSTRAINT ") :])
    elif action.startswith("DROP CONSTRAINT "):
        name = action[len("DROP CONSTRAINT ") :]
        definitions.difference_update(
            {
                definition
                for definition in definitions
                if definition.startswith(f"CONSTRAINT {name} ")
            }
        )
    else:
        raise AssertionError(
            f"this test does not know how to fold {statement!r} into a schema."
            " Teach it rather than letting it pass by ignoring the statement."
        )


def schema_from(statements: list[str]) -> Schema:
    """Reduce a list of DDL statements to the schema they leave behind."""
    schema: Schema = {_INDEXES: set()}
    for statement in statements:
        if statement.startswith("CREATE TABLE "):
            head, _, body = statement.partition("(")
            schema[head[len("CREATE TABLE ") :].strip()] = set(
                _split_definitions(body.rsplit(")", 1)[0])
            )
        elif statement.startswith(("CREATE INDEX ", "CREATE UNIQUE INDEX ")):
            schema[_INDEXES].add(statement)
        elif statement.startswith("ALTER TABLE "):
            _apply_alter(schema, statement)
        else:
            raise AssertionError(
                f"this test does not know how to fold {statement!r} into a schema."
                " Teach it rather than letting it pass by ignoring the statement."
            )
    return schema


def statements_from_models() -> list[str]:
    dialect = postgresql.dialect()
    rendered: list[str] = []
    for table in Base.metadata.sorted_tables:
        rendered.append(normalise(str(CreateTable(table).compile(dialect=dialect))))
        for index in table.indexes:
            rendered.append(normalise(str(CreateIndex(index).compile(dialect=dialect))))
    return rendered


def test_the_migrations_build_exactly_the_schema_the_models_declare() -> None:
    assert schema_from(statements_from_migrations()) == schema_from(statements_from_models())


def test_the_reducer_refuses_ddl_it_was_not_taught() -> None:
    # The guard that keeps the test above honest: the first version of this
    # file silently dropped every statement that was not a CREATE, which made
    # a schema change invisible to the check that exists to see it.
    with pytest.raises(AssertionError, match="does not know how to fold"):
        schema_from(
            [
                "CREATE TABLE data_source ( id BIGSERIAL NOT NULL )",
                "ALTER TABLE data_source ALTER COLUMN config SET NOT NULL",
            ]
        )


def test_the_reducer_applies_the_alters_it_does_know() -> None:
    # And the other half: the fold has to actually change the schema, or the
    # comparison above would be equally blind with the guard in place.
    schema = schema_from(
        [
            "CREATE TABLE data_source ( id BIGSERIAL NOT NULL, name VARCHAR(64) NOT NULL )",
            "ALTER TABLE data_source ADD COLUMN config JSONB",
            "ALTER TABLE data_source DROP COLUMN name",
        ]
    )

    assert schema["data_source"] == {"id BIGSERIAL NOT NULL", "config JSONB"}


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
