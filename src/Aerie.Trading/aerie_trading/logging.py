"""Structured JSON on stdout, in the shape the cluster's log pipeline already
reads.

There is no schema to invent here, and inventing one is the failure this module
exists to avoid: fluent-bit tails every container's stdout, parses the ``log``
field with a parser named ``dotnet_json``, and then runs two Lua passes that
derive ``service`` and ``aerie_revision`` from specific keys. Emit a different
shape and the lines still arrive in OpenSearch - as unparsed text, missing
exactly the two fields every saved search and dashboard filters on. See
``deploy/cluster/observability/controllers/fluent-bit.yaml`` and its
``fluent-bit/service_tag.lua``.

So the format below is .NET's ``AddJsonConsole`` output, reproduced field for
field:

.. code-block:: json

    {"LogLevel":"Information","Category":"aerie_trading.control.app",
     "EventId":0,"Message":"...","State":{"Message":"...","AerieRevision":"..."}}

Three details in that shape are load-bearing rather than cosmetic:

- **``State.Service`` is never set.** In ``service_tag.lua`` its presence is
  what marks a line as *relayed* - written by one workload on behalf of
  another, the way ``Aerie.Api`` relays browser log lines - and a relayed line
  is deliberately never attributed to the container image it came out of. This
  service speaks only for itself, so it leaves the field absent and lets the
  default branch attribute the line to the container name, ``trading``.
- **``State.AerieRevision`` is set on every line.** For a non-relayed line the
  Lua prefers this over the sha it parses out of the image tag. That override
  path exists in the script already and, in its own words, "nothing does this
  today; it is here so that an image deployed at a moving tag has a way to
  report a revision at all". This is the first thing that does - which means
  the field is right even before the image reaches a stamped tag.
- **``State.AerieSequence`` is a number.** The Lua runs it through
  ``tonumber`` and drops anything that is not positive.

Everything else a caller passes as ``extra`` lands inside ``State`` beside
those, which is where the .NET side puts its structured message properties.
"""

import json
import logging
import sys
from datetime import UTC, datetime
from typing import Any, Final

from aerie_trading.revision import Revision, read_revision

__all__ = ["AerieJsonFormatter", "configure_logging"]

#: Python's level names are not .NET's, and the log index is shared. Mapped
#: rather than lowercased so a query written against one service's lines
#: matches the other's.
_LEVEL_NAMES: Final[dict[int, str]] = {
    logging.CRITICAL: "Critical",
    logging.ERROR: "Error",
    logging.WARNING: "Warning",
    logging.INFO: "Information",
    logging.DEBUG: "Debug",
    logging.NOTSET: "Trace",
}

#: LogRecord attributes that are the logging module's own bookkeeping. Anything
#: else on the record was put there by a caller's ``extra=`` and belongs in
#: ``State``.
_RESERVED: Final[frozenset[str]] = frozenset(
    {
        "args",
        "asctime",
        # uvicorn's own: the same message again with ANSI colour codes in it,
        # attached to every line it logs. Useful in a terminal, noise in a log
        # index, and it would be the longest field on most lines.
        "color_message",
        "created",
        "exc_info",
        "exc_text",
        "filename",
        "funcName",
        "levelname",
        "levelno",
        "lineno",
        "module",
        "msecs",
        "message",
        "msg",
        "name",
        "pathname",
        "process",
        "processName",
        "relativeCreated",
        "stack_info",
        "taskName",
        "thread",
        "threadName",
    }
)


class AerieJsonFormatter(logging.Formatter):
    """One JSON object per line, in ``AddJsonConsole``'s shape."""

    def __init__(self, revision: Revision | None = None) -> None:
        super().__init__()
        self._revision = revision if revision is not None else read_revision()

    def format(self, record: logging.LogRecord) -> str:
        message = record.getMessage()

        state: dict[str, Any] = {
            # .NET puts the rendered message inside State as well as at the top
            # level. Kept, because a query written against one is a query that
            # works on both services' lines.
            "Message": message,
            "AerieRevision": self._revision.revision,
        }
        if self._revision.sequence > 0:
            state["AerieSequence"] = self._revision.sequence

        for key, value in record.__dict__.items():
            if key in _RESERVED or key.startswith("_"):
                continue
            state[key] = value

        payload: dict[str, Any] = {
            # The one deliberate addition to AddJsonConsole's shape, which
            # carries no timestamp unless one is configured. Nothing downstream
            # reads it - fluent-bit's dotnet_json parser declares no Time_Key,
            # so the record's time in OpenSearch is the one the tail input
            # assigns either way - but a JSON line read raw out of
            # `kubectl logs` is much worse without it.
            "Timestamp": datetime.fromtimestamp(record.created, tz=UTC)
            .isoformat(timespec="milliseconds")
            .replace("+00:00", "Z"),
            "LogLevel": _LEVEL_NAMES.get(record.levelno, "Information"),
            "Category": record.name,
            "EventId": 0,
            "Message": message,
            "State": state,
        }

        if record.exc_info is not None:
            payload["Exception"] = self.formatException(record.exc_info)
        elif record.exc_text:
            payload["Exception"] = record.exc_text

        if record.stack_info is not None:
            payload["StackTrace"] = self.formatStack(record.stack_info)

        # default=str rather than a custom encoder: an ``extra`` carrying
        # something unserialisable must not turn a log line into an exception
        # inside the logging machinery, which is where log records go to die
        # silently.
        return json.dumps(payload, default=str)


def configure_logging(level: str = "INFO", revision: Revision | None = None) -> None:
    """Point the root logger, and uvicorn's three, at one JSON handler on stdout.

    uvicorn installs its own handlers when it configures logging, and its
    access logger does not propagate by default - so a service that only
    configures the root logger ships beautifully structured application lines
    and plain-text request lines out of the same container. Both are cleared
    and re-parented here instead.

    stdout rather than stderr for everything, including errors: fluent-bit
    tails the container's combined output either way, and splitting the stream
    only decides which order two lines written in the same millisecond appear
    in when a human runs ``kubectl logs``.
    """
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(AerieJsonFormatter(revision))

    root = logging.getLogger()
    for existing in list(root.handlers):
        root.removeHandler(existing)
    root.addHandler(handler)
    root.setLevel(level)

    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        logger = logging.getLogger(name)
        for existing in list(logger.handlers):
            logger.removeHandler(existing)
        logger.propagate = True
