"""The shipped strategies, and the registry the rest of the system reads.

docs/plans/trading.md Phase 4 is explicit that these are **shipped components,
not test fixtures**: *"the owner's requirement is that the vertical arrives in
production demonstrating itself, which means the app boots with strategies
registered and results already in the leaderboard."* Phase 7's seed job reads
``REGISTRY`` and writes a ``strategy`` row per entry; Phase 5's worker looks a
name up here and builds an instance from a ``param_set``.

Two entries, and each has a job the other cannot do:

``buy_and_hold``
    the smallest honest example of the ``Strategy`` protocol, and the file to
    read before writing one. Phase 6 also needs it as the baseline beside every
    result, so it costs nothing.

``ma_crossover``
    the only one of the two that can exercise a parameter schema, a sweep
    launcher and a multi-run leaderboard. A UI with one unparameterized
    strategy in it does not demonstrate the product.

**Neither is a candidate for making money, and each says so in its own
``description``** so that the UI says so too rather than relying on whoever
writes the page to remember.

Registration is an explicit tuple rather than a decorator that populates a
module-level dict on import. A decorator makes "which strategies exist" depend
on which modules something happened to import, which is a question whose answer
differs between the seed job, the worker and the test suite - and the failure
is a strategy silently missing from the registry rather than an error.
"""

from __future__ import annotations

from collections.abc import Mapping
from typing import Final

from aerie_trading.engine.strategy import StrategySpec
from aerie_trading.strategies import buy_and_hold, ma_crossover

__all__ = ["REGISTRY", "SPECS", "spec_for"]

#: Every strategy this build ships, in registration order.
SPECS: Final[tuple[StrategySpec, ...]] = (buy_and_hold.SPEC, ma_crossover.SPEC)

#: The same, keyed by name - which is what a ``run`` row stores and what a
#: worker resolves. Built from ``SPECS`` rather than written twice, so the two
#: cannot disagree about what exists.
REGISTRY: Final[Mapping[str, StrategySpec]] = {spec.name: spec for spec in SPECS}


def spec_for(name: str) -> StrategySpec:
    """The spec registered under ``name``.

    Raises with the full list rather than a bare ``KeyError``: the caller is a
    worker that has just pulled a run off a queue, and the useful thing to log
    is "this build does not ship that strategy, it ships these" - which is the
    difference between a deploy that is behind and a name that is misspelled.
    """
    try:
        return REGISTRY[name]
    except KeyError:
        raise LookupError(
            f"no strategy named {name!r} in this build; it ships {sorted(REGISTRY)}"
        ) from None
