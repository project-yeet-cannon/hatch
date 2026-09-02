"""The log line's shape, asserted field by field.

This is not a test about formatting. Everything below is a property something
downstream depends on: fluent-bit's ``dotnet_json`` parser, the two Lua passes
in ``service_tag.lua`` that derive ``service`` and ``aerie_revision``, and the
OpenSearch index template that maps those as keywords. A line that is merely
*valid JSON* still loses both fields, and it loses them silently - the records
arrive, the dashboards go empty.
"""

import json
import logging
from typing import Any

import pytest

from aerie_trading.logging import AerieJsonFormatter
from tests.conftest import STAMPED


def render(record: logging.LogRecord, formatter: AerieJsonFormatter | None = None) -> Any:
    return json.loads((formatter or AerieJsonFormatter(STAMPED)).format(record))


def make_record(
    level: int = logging.INFO,
    message: str = "hello %s",
    args: tuple[object, ...] = ("world",),
    **extra: object,
) -> logging.LogRecord:
    record = logging.LogRecord(
        name="aerie_trading.test",
        level=level,
        pathname=__file__,
        lineno=1,
        msg=message,
        args=args,
        exc_info=None,
    )
    for key, value in extra.items():
        setattr(record, key, value)
    return record


def test_a_line_is_one_json_object() -> None:
    payload = render(make_record())

    assert payload["Category"] == "aerie_trading.test"
    assert payload["Message"] == "hello world"
    assert payload["State"]["Message"] == "hello world"
    assert payload["EventId"] == 0


@pytest.mark.parametrize(
    ("level", "expected"),
    [
        (logging.DEBUG, "Debug"),
        (logging.INFO, "Information"),
        (logging.WARNING, "Warning"),
        (logging.ERROR, "Error"),
        (logging.CRITICAL, "Critical"),
    ],
)
def test_levels_use_dot_nets_vocabulary(level: int, expected: str) -> None:
    # The log index is shared with Aerie.Api. A query for LogLevel:Warning has
    # to match both services' lines or it is worse than no query at all.
    assert render(make_record(level=level))["LogLevel"] == expected


def test_the_revision_is_on_every_line() -> None:
    # service_tag.lua's set_aerie_revision prefers State.AerieRevision over the
    # sha it parses out of the image tag, for a non-relayed line. That is what
    # makes aerie_revision correct even for an image deployed at a moving tag.
    state = render(make_record())["State"]

    assert state["AerieRevision"] == STAMPED.revision
    assert state["AerieSequence"] == STAMPED.sequence
    assert isinstance(state["AerieSequence"], int)


def test_an_unstamped_build_omits_the_sequence() -> None:
    # The Lua drops a non-positive sequence anyway; sending 0 would be a field
    # that exists and means nothing.
    from aerie_trading.revision import Revision

    state = render(make_record(), AerieJsonFormatter(Revision("dev", 0, None)))["State"]

    assert state["AerieRevision"] == "dev"
    assert "AerieSequence" not in state


def test_state_service_is_never_set() -> None:
    # Its presence is how service_tag.lua identifies a *relayed* line - one
    # workload writing on another's behalf, the way Aerie.Api relays browser
    # logs - and a relayed line is deliberately never attributed to the
    # container image it came out of. This service speaks only for itself.
    assert "Service" not in render(make_record())["State"]


def test_extra_fields_land_inside_state() -> None:
    state = render(make_record(Symbol="SPY", Rows=42))["State"]

    assert state["Symbol"] == "SPY"
    assert state["Rows"] == 42


def test_an_unserialisable_extra_does_not_break_the_line() -> None:
    # A log call must never be the thing that raises. Log records that fail to
    # format disappear into logging's own error handling, which is the worst
    # possible place for the one line explaining an outage.
    payload = render(make_record(Thing=object()))

    assert "object" in payload["State"]["Thing"]


def test_an_exception_is_rendered_into_its_own_field() -> None:
    try:
        raise ValueError("boom")
    except ValueError:
        record = make_record(level=logging.ERROR, message="failed", args=())
        import sys

        record.exc_info = sys.exc_info()

    payload = render(record)

    assert "ValueError: boom" in payload["Exception"]


def test_the_timestamp_is_utc_iso_8601() -> None:
    timestamp = render(make_record())["Timestamp"]

    assert timestamp.endswith("Z")
    assert "T" in timestamp
