"""The Ledger's rows for things the code already defines.

Three tables here are caches of something whose source of truth is elsewhere:
``strategy`` mirrors ``strategies.REGISTRY``, ``param_set`` names a point in a
space the strategy's own model declares, and ``instrument`` names something the
lake already has bars for. None of them is the definition; each exists so a
``run`` can point a foreign key at it, and so a leaderboard from a build that
no longer ships a strategy still knows what it was.

Every function here is **idempotent and does not commit**, matching
``providers/registry.ensure_data_source`` - which this file is deliberately
shaped after. The caller owns the transaction, because these rows are written
in the same one as the run they are for; a helper that committed on its own
would leave a ``param_set`` behind for a sweep that then failed to enqueue.
"""

from __future__ import annotations

import hashlib
import json
from collections.abc import Mapping
from decimal import Decimal
from typing import Any, cast

from sqlalchemy import select
from sqlalchemy.orm import Session

from aerie_trading.db.models import Instrument, InstrumentKind, ParamSet, Strategy
from aerie_trading.engine.instruments import Equity, OptionContract
from aerie_trading.engine.instruments import Instrument as EngineInstrument
from aerie_trading.engine.strategy import StrategySpec

__all__ = [
    "canonical_params",
    "ensure_instrument",
    "ensure_param_set",
    "ensure_strategy",
    "params_hash",
]


def canonical_params(params: Mapping[str, object]) -> str:
    """One parameter set, rendered so that two equal ones render identically.

    Sorted keys, no whitespace, and every value already through pydantic's
    ``mode="json"`` dump before it gets here - which is what turns a ``Decimal``
    into a string rather than into a float whose last digits depend on how it
    was built. The rendering is what is hashed, so this function is the
    definition of "the same parameters" for the whole silo.
    """
    return json.dumps(dict(params), sort_keys=True, separators=(",", ":"))


def params_hash(params: Mapping[str, object]) -> str:
    """sha256 over ``canonical_params``. What ``param_set`` is unique on."""
    return hashlib.sha256(canonical_params(params).encode("utf-8")).hexdigest()


def ensure_strategy(session: Session, spec: StrategySpec) -> Strategy:
    """Insert or refresh the ``strategy`` row for ``spec``, and return it.

    Refreshed rather than left alone, for the reason ``ensure_data_source``
    overwrites a provider's config: a row describing an older version of a
    strategy that has since been edited is worse than no row, because it is
    confidently wrong about what the code does *now*. The history of what a
    strategy was then is carried by the ``aerie_revision`` on each run, which
    is the column that can honestly hold it.
    """
    existing = session.scalar(select(Strategy).where(Strategy.name == spec.name))
    schema = _jsonable(spec.params_model.model_json_schema())

    if existing is None:
        created = Strategy(
            name=spec.name,
            description=spec.description,
            params_schema=schema,
        )
        session.add(created)
        return created

    existing.description = spec.description
    existing.params_schema = schema
    return existing


def _jsonable(value: object) -> Any:
    """A pydantic JSON schema, with its numeric bounds as JSON numbers.

    ``swept()`` declares a field's ``ge`` and ``le`` as ``Decimal`` - which is
    deliberate, because a step of 0.1 walked in binary produces parameter
    values that are distinct rows from the ones a person typed. pydantic
    carries those Decimals straight into ``minimum`` and ``maximum``, and
    ``Decimal`` is not JSON, so the schema cannot be written to JSONB as it
    comes out.

    Converted to numbers rather than to strings, because JSON Schema says
    ``minimum`` is a number and a form renderer reading a string there is
    entitled to refuse it. The exact declared range survives regardless: it is
    carried separately, as strings, in the ``sweep`` block ``swept()`` writes
    into ``json_schema_extra`` - which is the copy Phase 5 walks and Phase 7
    renders, and the reason this conversion can afford to be lossy.
    """
    if isinstance(value, Decimal):
        # Integral bounds stay integers. A `minimum` of 2.0 where the field is
        # an int is the sort of thing that shows up in a UI as "2.0".
        return int(value) if value == value.to_integral_value() else float(value)
    if isinstance(value, dict):
        return {key: _jsonable(entry) for key, entry in cast("dict[str, object]", value).items()}
    if isinstance(value, list):
        return [_jsonable(entry) for entry in cast("list[object]", value)]
    return value


def ensure_param_set(
    session: Session, strategy: Strategy, params: Mapping[str, object]
) -> ParamSet:
    """Insert or find the ``param_set`` row for ``params`` under ``strategy``.

    Idempotent by hash, which is what makes re-running a sweep over a wider
    window reuse its variations instead of writing ten thousand duplicates that
    a leaderboard would then group by. ``params`` must already be JSON-shaped -
    a ``model_dump(mode="json")`` - because the hash is over the rendering and
    a ``Decimal`` hashed as itself would not match the same value read back out
    of JSONB.

    ``session.flush()`` before the lookup is deliberate: a sweep's expansion
    can contain the same parameter set twice only if the grid does, but a
    *second* sweep in the same transaction legitimately reuses the first's, and
    without the flush the pending row is invisible to the query.
    """
    digest = params_hash(params)
    session.flush()
    existing = session.scalar(
        select(ParamSet).where(
            ParamSet.strategy_id == strategy.id,
            ParamSet.params_hash == digest,
        )
    )
    if existing is not None:
        return existing

    created = ParamSet(strategy=strategy, params=dict(params), params_hash=digest)
    session.add(created)
    return created


def ensure_instrument(session: Session, instrument: EngineInstrument) -> Instrument:
    """Insert or find the ``instrument`` row for one engine instrument.

    Called once per distinct instrument in a run's blotter, which for an equity
    sweep is once per symbol per worker process and never again - the row is
    found rather than written on every run after the first.

    The equity and option branches fill in different columns, and the CHECK
    constraint in ``db/models.Instrument`` is what makes that a schema rule
    rather than a convention this function has to be trusted about: an option
    has all four of its defining fields or the insert fails.
    """
    session.flush()
    existing = session.scalar(select(Instrument).where(Instrument.symbol == instrument.symbol))
    if existing is not None:
        return existing

    if isinstance(instrument, Equity):
        created = Instrument(
            symbol=instrument.symbol,
            kind=InstrumentKind.EQUITY.value,
            multiplier=instrument.multiplier,
        )
    else:
        contract: OptionContract = instrument
        created = Instrument(
            symbol=contract.symbol,
            kind=InstrumentKind.OPTION.value,
            underlying_symbol=contract.underlying_symbol,
            expiry=contract.expiry,
            strike=contract.strike,
            option_right=contract.right.value,
            multiplier=contract.multiplier,
        )
    session.add(created)
    return created
