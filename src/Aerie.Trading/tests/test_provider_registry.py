"""Registering a provider in the Ledger, and what the row has to carry.

The session here is a stand-in that records what it was asked to do - the same
idiom as ``StubDatabase``. What is under test is the decision, not SQLAlchemy:
whether the first caller inserts, whether the second updates rather than
duplicating, and whether the provenance that lands in the row is enough to
reproduce the numbers it describes. The last one is the whole reason the column
exists. The *concurrent* case - two collectors registering at the same instant -
is not a decision and cannot be stubbed; it is in
``tests/test_catalog_races.py``, against a real Postgres.
"""

from collections.abc import Generator
from contextlib import contextmanager
from typing import Any

import pytest

from aerie_trading.db.models import DataSource
from aerie_trading.providers.base import CHAINS_ARE_PRICEABLE
from aerie_trading.providers.registry import ensure_data_source
from aerie_trading.providers.synthetic.config import (
    SYNTHETIC_GENERATOR_VERSION,
    SyntheticConfig,
)
from aerie_trading.providers.synthetic.provider import (
    SYNTHETIC_SOURCE_NAME,
    SyntheticMarketDataProvider,
)


class StubSession:
    """A ``Session`` that answers one query and remembers what was added."""

    def __init__(self, existing: DataSource | None = None) -> None:
        self.existing = existing
        self.added: list[DataSource] = []
        self.queries = 0
        self.flushes = 0
        self.savepoints = 0

    def scalar(self, _statement: Any) -> DataSource | None:
        self.queries += 1
        return self.existing

    def add(self, instance: DataSource) -> None:
        self.added.append(instance)

    def flush(self) -> None:
        self.flushes += 1

    @contextmanager
    def begin_nested(self) -> Generator[None]:
        """The savepoint ``db/upsert.insert_or_find`` opens around its insert.

        A no-op here, and that is the honest stand-in: a savepoint is a thing
        Postgres does, and the branch it protects - an insert that loses a race
        to another connection - is not reachable from a stub with one
        connection and no concurrency. Present so the happy path runs; the
        branch is tested where it can be.
        """
        self.savepoints += 1
        yield


@pytest.fixture
def provider() -> SyntheticMarketDataProvider:
    return SyntheticMarketDataProvider()


def test_the_first_registration_inserts_the_row(
    provider: SyntheticMarketDataProvider,
) -> None:
    session = StubSession()

    registered = ensure_data_source(session, provider)  # pyright: ignore[reportArgumentType]

    assert session.added == [registered]
    assert registered.name == SYNTHETIC_SOURCE_NAME
    assert registered.description == provider.description


def test_registering_twice_updates_rather_than_duplicates(
    provider: SyntheticMarketDataProvider,
) -> None:
    # Every process that touches a provider registers at startup - a collector,
    # a worker, a backfill - so the second arrival has to be a no-op rather
    # than a unique-constraint failure or a second row that half the data then
    # points at.
    existing = DataSource(name=SYNTHETIC_SOURCE_NAME, description="stale", config={"old": True})
    session = StubSession(existing)

    registered = ensure_data_source(session, provider)  # pyright: ignore[reportArgumentType]

    assert registered is existing
    assert session.added == []
    assert registered.description == provider.description
    assert registered.config == dict(provider.provenance)


def test_the_recorded_provenance_reproduces_the_provider(
    provider: SyntheticMarketDataProvider,
) -> None:
    # "So that a run is reproducible from its provenance alone." A row that
    # records the source's name but not its seed describes which dice were
    # rolled and not what they said.
    session = StubSession()

    config = ensure_data_source(session, provider).config  # pyright: ignore[reportArgumentType]

    assert config is not None
    assert config["generator_version"] == SYNTHETIC_GENERATOR_VERSION
    assert config[CHAINS_ARE_PRICEABLE] is False
    assert SyntheticConfig.model_validate(config["config"]) == provider.config


def test_the_provenance_is_json_and_holds_no_secret(
    provider: SyntheticMarketDataProvider,
) -> None:
    # It goes into a JSONB column and is read by a person during an incident.
    # Neither of those tolerates a Python object, and the second is the reason
    # a provider's credential never appears here - the synthetic source has
    # none, and Phase 8's provider inherits the rule rather than discovering it.
    import json

    rendered = json.dumps(dict(provider.provenance))

    assert "password" not in rendered.lower()
    assert "token" not in rendered.lower()
    assert json.loads(rendered)["provider"] == SYNTHETIC_SOURCE_NAME
