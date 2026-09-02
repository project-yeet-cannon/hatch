"""Alembic's entry point, wired to the same configuration the service uses.

Two departures from the generated template, both deliberate:

- **The URL comes from ``Settings``, not from ``alembic.ini``.** One place a
  connection string is assembled - see that file's header.
- **``fileConfig`` is not called.** The template's logging block would replace
  the JSON handler ``aerie_trading.logging`` installs with plain text, on
  exactly the lines an operator reads when a deploy stalls in its init
  container.
"""

from alembic import context
from sqlalchemy import create_engine

from aerie_trading.db.models import Base
from aerie_trading.logging import configure_logging
from aerie_trading.settings import get_settings

settings = get_settings()
configure_logging(settings.log_level)

# What `alembic revision --autogenerate` diffs the database against. Every
# model inherits from this one Base for exactly this reason.
target_metadata = Base.metadata


def run_migrations_offline() -> None:
    """Render SQL to stdout instead of applying it - ``alembic upgrade --sql``.

    Kept because it is how a migration gets reviewed before it runs against a
    database that matters, which is a habit worth having before there is data
    to lose.
    """
    context.configure(
        # Credentials are omitted: offline mode opens no connection, it renders
        # SQL, and a password does not belong in something whose whole output
        # is meant to be pasted into a review.
        url=settings.database_url,
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        compare_type=True,
    )

    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    """Apply migrations against the Ledger."""
    engine = create_engine(settings.database_url, connect_args=settings.connect_args)

    with engine.connect() as connection:
        context.configure(
            connection=connection,
            target_metadata=target_metadata,
            # Off by default in Alembic, and wrong to leave off: a column whose
            # type changed in models.py is otherwise invisible to autogenerate,
            # so the migration is silently empty and the drift is found by a
            # query that fails months later.
            compare_type=True,
        )

        with context.begin_transaction():
            context.run_migrations()

    engine.dispose()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
