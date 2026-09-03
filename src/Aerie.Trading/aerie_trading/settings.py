"""Every value this process reads from its environment, in one typed model.

Two conventions worth stating, because both are choices:

- **The database is addressed with libpq's own names** - ``PGHOST``,
  ``PGPORT``, ``PGDATABASE``, ``PGUSER``, ``PGPASSWORD`` - and not with a
  single ``DATABASE_URL``. That is the shape CNPG's generated ``-app`` Secret
  already publishes key-for-key (see
  ``deploy/cluster/trading/app/deployment.yaml``, and
  ``deploy/cluster/data/schema/quartz-ddl-job.yaml`` for the same block on the
  .NET side), so the manifest hands over exactly what the operator generated
  rather than assembling a URL that then has to be quoted correctly. It is also
  what ``psql`` in a debugging shell reads, with no second spelling to learn.
- **Everything else is prefixed ``TRADING_``.** The silo shares a cluster with
  workloads that have their own opinions about unprefixed names.

Nothing here has a *production* default that would let a misconfigured pod come
up looking healthy: the database values default to what a developer's local
Postgres would be, which fails visibly in a cluster rather than silently
connecting to something wrong.
"""

from functools import lru_cache
from typing import Literal

from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

# The module rather than the package: importing
# `aerie_trading.providers.synthetic` itself is free, and is kept that way on
# purpose - see that package's docstring. Every process in the silo reaches
# this file, and not all of them can afford pandas.
from aerie_trading.providers.synthetic.config import SyntheticConfig

__all__ = ["Settings", "get_settings"]

LogLevel = Literal["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"]


class Settings(BaseSettings):
    """Configuration, read once at startup."""

    model_config = SettingsConfigDict(
        env_prefix="TRADING_",
        # The environment is the only source. No .env file is read in the
        # cluster, and reading one here would be a second place a value could
        # come from that nothing in deploy/ can see.
        env_file=None,
        extra="ignore",
        # Nested models are addressed with a double underscore, so the
        # generator's seed is TRADING_SYNTHETIC__SEED. The whole model can also
        # be replaced at once with JSON in TRADING_SYNTHETIC, which is how a
        # different universe arrives without a rebuild.
        env_nested_delimiter="__",
    )

    # -- Postgres -----------------------------------------------------------
    # validation_alias rather than the TRADING_ prefix: these are libpq's
    # names, and the whole point is that they are the ones everything else
    # already uses.
    pg_host: str = Field(default="localhost", validation_alias="PGHOST")
    pg_port: int = Field(default=5432, validation_alias="PGPORT")
    pg_database: str = Field(default="trading", validation_alias="PGDATABASE")
    pg_user: str = Field(default="trading", validation_alias="PGUSER")
    pg_password: str = Field(default="", validation_alias="PGPASSWORD")

    # -- Process ------------------------------------------------------------
    log_level: LogLevel = "INFO"

    # Where to send a browser that the auth wall bounced here with nowhere to
    # land - see aerie_trading/control/app.py. Empty means "register no such
    # route", which is right for local development and for an installation
    # running with the wall off. A full URL rather than a domain: this silo
    # should not know how Aerie spells the path to its sign-in shell.
    sign_in_url: str = ""

    # -- Market data --------------------------------------------------------
    # The synthetic generator's whole configuration (docs/plans/trading.md
    # Phase 2 asks for "universe, seed, starting price level, drift and
    # volatility, all values rather than code"). Defaulted rather than
    # required, because the defaults are a working universe and a source that
    # needs configuring before it produces anything is one more thing standing
    # between a fresh clone and a backtest.
    #
    # It is carried here rather than constructed at each call site so that the
    # provenance recorded on the `data_source` row is the configuration this
    # *process* is running under, which is the only one a run can honestly
    # claim to be reproducible from.
    synthetic: SyntheticConfig = SyntheticConfig()

    @property
    def database_url(self) -> str:
        """A SQLAlchemy URL built from the parts above.

        ``postgresql+psycopg`` names psycopg 3 explicitly. SQLAlchemy's bare
        ``postgresql://`` still defaults to psycopg2, which is not installed
        here - the failure is an import error at first connect, which is to say
        at the first readiness probe rather than at startup.

        Credentials are passed as connect arguments rather than interpolated
        into the URL, so a password containing a URL-significant character is
        not a quoting problem waiting to happen. That is the same hazard
        ``charts/aerie/templates/_helpers.tpl`` writes out at length for the
        .NET connection string, solved structurally instead of by quoting.
        """
        return f"postgresql+psycopg://{self.pg_host}:{self.pg_port}/{self.pg_database}"

    @property
    def connect_args(self) -> dict[str, str]:
        return {"user": self.pg_user, "password": self.pg_password}


@lru_cache(maxsize=1)
def get_settings() -> Settings:
    """The process's settings, resolved once.

    Cached because configuration is immutable for the life of the process and
    re-reading it cannot produce a new answer. Tests that need different values
    construct ``Settings`` directly rather than clearing this cache.
    """
    return Settings()
