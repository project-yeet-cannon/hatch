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


def test_the_generators_configuration_is_environment_addressable(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # docs/plans/trading.md Phase 2 asks for the generator's universe, seed,
    # drift and volatility as "values rather than code". A pydantic model with
    # defaults is only half of that - the other half is being able to re-seed
    # an installation without rebuilding an image, which is what the nested
    # delimiter buys.
    monkeypatch.setenv("TRADING_SYNTHETIC__SEED", "4242")
    monkeypatch.setenv("TRADING_SYNTHETIC__ANNUAL_DRIFT", "0.05")

    settings = Settings()

    assert settings.synthetic.seed == 4242
    assert settings.synthetic.annual_drift == 0.05


def test_the_whole_universe_can_be_replaced_at_once(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # The model as JSON, for an installation that wants a different market
    # rather than a differently-seeded one.
    monkeypatch.setenv(
        "TRADING_SYNTHETIC",
        '{"seed": 5, "universe": [{"symbol": "zvzzt", "start_price": 12.5}]}',
    )

    settings = Settings()

    assert settings.synthetic.seed == 5
    # Upper-cased on the way in, because a ticker is not case-sensitive and a
    # universe that answers to one spelling and not the other is a lookup
    # failure waiting for the first lower-case watchlist entry.
    assert settings.synthetic.symbols == ("ZVZZT",)


def test_a_universe_with_a_repeated_symbol_fails_at_startup(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # Loudly, and before anything reads a price. `SyntheticConfig.symbol`
    # returns the first match, so a duplicate is a silently-ignored
    # configuration entry - which is the shape of bug that is found by
    # wondering why a parameter change did nothing.
    monkeypatch.setenv(
        "TRADING_SYNTHETIC",
        '{"universe": [{"symbol": "ZVZZT", "start_price": 1.0},'
        ' {"symbol": "zvzzt", "start_price": 2.0}]}',
    )

    with pytest.raises(ValueError, match="duplicate symbols"):
        Settings()
