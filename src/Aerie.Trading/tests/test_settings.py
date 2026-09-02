"""Configuration: where each value comes from, and what it becomes."""

import pytest

from aerie_trading.settings import Settings


def test_the_database_is_addressed_with_libpqs_own_names(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # PGHOST and friends, unprefixed, because that is what CNPG's generated
    # -app Secret publishes key for key and what psql reads in a debugging
    # shell. See deploy/cluster/trading/app/deployment.yaml.
    monkeypatch.setenv("PGHOST", "trading-pg-rw.trading.svc.cluster.local")
    monkeypatch.setenv("PGPORT", "5432")
    monkeypatch.setenv("PGDATABASE", "trading")
    monkeypatch.setenv("PGUSER", "trading")
    monkeypatch.setenv("PGPASSWORD", "a;password=with'punctuation")

    settings = Settings()

    assert settings.database_url == (
        "postgresql+psycopg://trading-pg-rw.trading.svc.cluster.local:5432/trading"
    )
    # The credentials are never in the URL. A generated password containing a
    # URL-significant character is otherwise a quoting problem that presents as
    # an authentication failure - the same hazard charts/aerie's _helpers.tpl
    # writes out at length for the .NET connection string, removed structurally
    # here rather than by quoting.
    assert settings.connect_args == {
        "user": "trading",
        "password": "a;password=with'punctuation",
    }


def test_the_driver_is_named_explicitly() -> None:
    # SQLAlchemy's bare postgresql:// still means psycopg2, which is not
    # installed here - the failure would be an import error at the first
    # readiness probe rather than at startup.
    assert Settings().database_url.startswith("postgresql+psycopg://")


def test_everything_that_is_not_postgres_is_prefixed(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("TRADING_LOG_LEVEL", "DEBUG")
    monkeypatch.setenv("TRADING_SIGN_IN_URL", "https://home.example.com/apps/auth/")

    settings = Settings()

    assert settings.log_level == "DEBUG"
    assert settings.sign_in_url == "https://home.example.com/apps/auth/"


def test_a_misspelled_log_level_fails_at_startup(monkeypatch: pytest.MonkeyPatch) -> None:
    # Loudly, and before the first request. A level nobody validates is a
    # service that silently logs nothing, which is discovered during the
    # incident it would have explained.
    monkeypatch.setenv("TRADING_LOG_LEVEL", "VERBOSE")

    with pytest.raises(ValueError, match="log_level"):
        Settings()
