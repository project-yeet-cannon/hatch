"""Insert a row, or find the one a concurrent writer inserted first.

Every ``ensure_*`` helper in this silo has the same shape - look for a row by
its natural key, and insert it if it is not there - and every one of them was
written with a comment saying it is idempotent. **Select-then-insert is not
idempotent under concurrency**, and the gap between the two statements is
exactly where two workers starting a sweep at the same moment both look, both
miss, and both insert:

    duplicate key value violates unique constraint "uq_instrument_symbol"

That is not a hypothetical. It is how the thousand-run sweep gate fails on a
loaded CI runner and passes on a developer's laptop: four workers claim their
first run within milliseconds of each other, finish it within milliseconds of
each other, and arrive at ``ensure_instrument`` for the same symbol at once.
The window is microseconds wide, which is why it is intermittent, and being
intermittent is the reason it is worth fixing structurally rather than by
serialising the callers.

**A savepoint, not a lock and not ``ON CONFLICT``.**

- A lock - advisory, or ``SELECT ... FOR UPDATE`` on a row that does not exist
  yet - would serialise every worker's completion behind one mutex to protect
  an insert that happens once per symbol per installation.
- ``INSERT ... ON CONFLICT DO NOTHING`` is the shorter answer and the wrong one
  here: these callers hand back an ORM instance that the caller then reads
  (``.id``) or mutates (``ensure_strategy`` refreshes an existing row), and a
  Core insert would return a primary key with no object behind it.
- A savepoint keeps the ORM semantics and, crucially, **keeps the enclosing
  transaction alive.** In Postgres a constraint violation aborts the whole
  transaction, and the enclosing transaction here is the one recording a run's
  status, its blotter and its metrics. Without the savepoint, recovering from
  the duplicate would mean losing the run that hit it.

**The retry is correct at READ COMMITTED**, which is what this database runs.
If the racing writer has not committed yet, our insert *blocks* on its index
entry rather than failing, and resolves when it commits or rolls back. If it
has committed, we get the violation - and the lookup that follows takes a fresh
snapshot, so it sees the row that beat us. There is no third outcome, and no
loop is needed: the second attempt cannot lose the same race twice, because the
row it lost to is now committed and visible.

**What it costs.** One ``SAVEPOINT`` and one ``RELEASE`` per *inserted* row -
nothing on the path where the row was already there, which is every call after
the first. The one place that pays it in bulk is ``enqueue_sweep``, which
inserts a ``param_set`` per grid point; two extra round trips over a local
socket, ten thousand times, against a sweep that is about to spend minutes in
the engine.
"""

from __future__ import annotations

from collections.abc import Callable

from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

__all__ = ["insert_or_find"]


def insert_or_find[Row](session: Session, created: Row, find: Callable[[], Row | None]) -> Row:
    """Add ``created`` and flush it, or return what ``find()`` turns up instead.

    ``find`` is the *same* lookup the caller already did before deciding to
    insert, passed as a callable so it can be run again after the race is lost.
    Taking the lookup rather than a key is what keeps this helper ignorant of
    which column is unique - ``instrument`` keys on a symbol, ``param_set`` on a
    hash within a strategy - and what stops it growing a parameter per table.

    Re-raises if ``find`` still comes back empty. A unique violation on a
    constraint the caller's lookup does not cover is a different bug wearing
    the same exception, and swallowing it would turn it into a ``None`` at some
    later line.
    """
    try:
        with session.begin_nested():
            session.add(created)
            # Inside the savepoint deliberately: the flush is what sends the
            # INSERT, so a flush outside it would put the violation in the
            # enclosing transaction, which is the state this helper exists to
            # avoid.
            session.flush()
    except IntegrityError:
        existing = find()
        if existing is None:
            raise
        return existing
    return created
