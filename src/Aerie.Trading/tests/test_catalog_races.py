"""Two writers reaching for the same row at the same instant.

The bug this file is written around was intermittent for the worst reason: the
window between "look for the row" and "insert the row" is microseconds wide, so
it never opened on a developer's laptop and opened regularly on a loaded CI
runner, where four workers claim their first run within milliseconds of each
other and finish it within milliseconds of each other. It surfaced as

    duplicate key value violates unique constraint "uq_instrument_symbol"

taking down a run that had already been computed correctly.

**Two kinds of test, answering two different questions.** The first kind is
deterministic: it puts a committed row in the way and requires the recovery
path to find it, so the mechanism is asserted rather than hoped for. The second
runs the real helpers from several threads at once, which is the shape of the
original failure.

Only one of the two threaded tests reliably collides, and it is worth knowing
which. ``ensure_param_set`` has a strategy lookup and a flush between its own
look and its write, so the window is wide enough that eight threads released
together always overlap in it - that test fails without the fix. The
``ensure_instrument`` window is a single statement wide and often does not, so
that test can pass against the broken code; it is here because it is the
failure as it actually occurred, not because it is the proof. The deterministic
tests are the proof.
"""

from __future__ import annotations

import threading
from collections.abc import Callable

import pytest
from sqlalchemy import select, text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.db.models import Instrument, InstrumentKind, ParamSet, Strategy
from aerie_trading.db.upsert import insert_or_find
from aerie_trading.engine.instruments import equity
from aerie_trading.runs.catalog import ensure_instrument, ensure_param_set, ensure_strategy
from aerie_trading.strategies import spec_for

WORKERS = 8


def committed_instrument(ledger: Engine, symbol: str) -> None:
    """A row somebody else wrote and committed, from another connection."""
    with Session(ledger) as other, other.begin():
        other.add(Instrument(symbol=symbol, kind=InstrumentKind.EQUITY.value, multiplier=1))


# -- the mechanism, deterministically ---------------------------------------


def test_an_insert_that_loses_the_race_finds_the_winner(ledger: Engine) -> None:
    committed_instrument(ledger, "ZVZZT")

    with Session(ledger) as session, session.begin():
        found = insert_or_find(
            session,
            # The row this session decided to write, before it knew.
            Instrument(symbol="ZVZZT", kind=InstrumentKind.EQUITY.value, multiplier=1),
            lambda: session.scalar(select(Instrument).where(Instrument.symbol == "ZVZZT")),
        )

        assert found.id is not None
        assert found.symbol == "ZVZZT"

    with ledger.connect() as connection:
        rows = connection.execute(
            text("SELECT COUNT(*) FROM instrument WHERE symbol = 'ZVZZT'")
        ).scalar_one()
    assert rows == 1


def test_the_transaction_that_lost_the_race_is_still_usable(ledger: Engine) -> None:
    # The reason this is a savepoint rather than a bare try/except. In Postgres
    # a constraint violation aborts the whole transaction, and the enclosing
    # transaction on the path where this actually happens is the one recording
    # a run's status, its blotter and its metrics. Recovering from the
    # duplicate has to leave that transaction able to commit.
    committed_instrument(ledger, "ZVZZT")

    with Session(ledger) as session, session.begin():
        insert_or_find(
            session,
            Instrument(symbol="ZVZZT", kind=InstrumentKind.EQUITY.value, multiplier=1),
            lambda: session.scalar(select(Instrument).where(Instrument.symbol == "ZVZZT")),
        )
        session.add(Instrument(symbol="ZWZZT", kind=InstrumentKind.EQUITY.value, multiplier=1))

    with ledger.connect() as connection:
        symbols = set(connection.execute(text("SELECT symbol FROM instrument")).scalars().all())
    assert symbols == {"ZVZZT", "ZWZZT"}


def test_a_violation_the_lookup_does_not_explain_is_re_raised(ledger: Engine) -> None:
    # A unique violation on a constraint the caller's lookup does not cover is
    # a different bug wearing the same exception. Swallowing it would turn it
    # into a None several lines later.
    committed_instrument(ledger, "ZVZZT")

    with (
        Session(ledger) as session,
        session.begin(),
        pytest.raises(Exception, match="uq_instrument_symbol"),
    ):
        insert_or_find(
            session,
            Instrument(symbol="ZVZZT", kind=InstrumentKind.EQUITY.value, multiplier=1),
            lambda: None,
        )


# -- the real helpers, concurrently -----------------------------------------


def race(ledger: Engine, work: Callable[[Session], object]) -> list[BaseException]:
    """Run ``work`` in its own transaction on ``WORKERS`` threads at once.

    The barrier is what makes the collision likely rather than theoretical:
    without it the threads start far enough apart that the first one has
    committed before the second one looks, which is the laptop behaviour that
    hid the bug in the first place.
    """
    barrier = threading.Barrier(WORKERS)
    failures: list[BaseException] = []
    lock = threading.Lock()

    def run() -> None:
        try:
            with Session(ledger) as session:
                barrier.wait(timeout=10)
                with session.begin():
                    work(session)
        except BaseException as failure:  # recorded rather than raised, then asserted on
            with lock:
                failures.append(failure)

    threads = [threading.Thread(target=run) for _ in range(WORKERS)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join(timeout=30)
    return failures


def test_concurrent_workers_reaching_for_one_instrument_produce_one_row(
    ledger: Engine,
) -> None:
    failures = race(ledger, lambda session: ensure_instrument(session, equity("ZVZZT")))

    assert failures == []
    with ledger.connect() as connection:
        rows = connection.execute(text("SELECT COUNT(*) FROM instrument")).scalar_one()
    assert rows == 1


def test_concurrent_launchers_reaching_for_one_param_set_produce_one_row(
    ledger: Engine,
) -> None:
    def ensure(session: Session) -> object:
        strategy = ensure_strategy(session, spec_for("ma_crossover"))
        session.flush()
        # Already JSON-shaped, as ``ensure_param_set`` requires: the hash is
        # over the rendering, and a Decimal hashed as itself would not match
        # the same value read back out of JSONB.
        return ensure_param_set(session, strategy, {"fast": 5, "slow": 20})

    failures = race(ledger, ensure)

    assert failures == []
    with Session(ledger) as session:
        assert len(session.scalars(select(Strategy)).all()) == 1
        assert len(session.scalars(select(ParamSet)).all()) == 1
