"""The commit this build came from - the trading silo's half of
[`docs/plans/version.md`](../../../docs/plans/version.md).

That document's framing is the whole design here: **the revision is a property
of the build, not of the deployment.** It is stamped into the artifact when the
artifact is produced and is thereafter read, never supplied. A pod that can be
told its own revision by its environment is a pod that can be told the wrong
one, and every consumer of this value - an operator watching a rollout, a run
record claiming which code produced it - is trusting it to be the truth about
the code that is actually running.

.NET gets that for free: ``Dockerfile.api`` passes ``-p:SourceRevisionId`` and
the SDK writes the sha into the assembly. Python has no assembly, so the
equivalent is a data file written *into the package* before it is installed
(see ``Dockerfile.trading``) rather than an environment variable on the
container. An env var would be read from the deployment, which is exactly the
property the .NET side went out of its way not to have.

An unstamped build - `uv run`, a test, anything outside the image - reports
``dev`` and sequence 0, matching ``AerieRevision.Development`` on the .NET side
and the Vite plugin's constant on the web side, so a development build reads
the same on every surface.
"""

import json
from dataclasses import dataclass
from datetime import datetime
from importlib.resources import files
from typing import Any, Final, cast

__all__ = ["BUILD_FILE_NAME", "DEVELOPMENT", "Revision", "read_revision", "revision_from_mapping"]

#: What an unstamped build reports. Identical to the constant on the other two
#: surfaces, deliberately - see the module docstring.
DEVELOPMENT: Final = "dev"

#: Written by ``Dockerfile.trading`` into the package directory, so it is part
#: of the installed distribution rather than part of the environment.
BUILD_FILE_NAME: Final = "build.json"


@dataclass(frozen=True)
class Revision:
    """What this build is, in the vocabulary docs/plans/version.md fixes.

    ``sequence`` is ``git rev-list --count HEAD`` at build time: a sha has no
    order, and "is this behind that" is the question an operator actually asks
    during a rollout.
    """

    revision: str
    sequence: int
    built_at: datetime | None

    @property
    def is_development(self) -> bool:
        return self.revision == DEVELOPMENT


def read_revision() -> Revision:
    """Read the stamp, or report a development build.

    Every failure mode - no file, unreadable file, malformed JSON, a sha that
    is not a sha - lands on ``dev`` rather than raising. A malformed stamp is a
    build-pipeline bug, and refusing to start over it would take the service
    down in order to withhold something this endpoint can simply say out loud.
    """
    try:
        raw = files(__package__ or "aerie_trading").joinpath(BUILD_FILE_NAME).read_text()
        parsed: Any = json.loads(raw)
    except (FileNotFoundError, OSError, ValueError):
        return Revision(DEVELOPMENT, 0, None)

    return revision_from_mapping(parsed)


def revision_from_mapping(parsed: object) -> Revision:
    """The parsing half, split out from the reading half so it can be tested.

    A build stamp is read exactly once, in a container, on a path nobody
    watches - which is precisely the kind of code that is discovered to be
    wrong by a version endpoint reporting ``dev`` in production. Everything it
    can be handed is a value a test can hand it.
    """
    if not isinstance(parsed, dict):
        return Revision(DEVELOPMENT, 0, None)

    # JSON object keys are strings by construction; the cast is what says so to
    # a checker that only sees `dict`.
    stamped = cast(dict[str, Any], parsed)
    return Revision(
        revision=_parse_revision(stamped.get("revision")),
        sequence=_parse_sequence(stamped.get("sequence")),
        built_at=_parse_built_at(stamped.get("builtAt")),
    )


def _parse_revision(value: Any) -> str:
    """A full 40-character sha, or ``dev``.

    Truncation is checked rather than assumed away: the image tags carried a
    7-character sha before docs/plans/version.md widened them, and
    half-migrating to the full one is a plausible mistake that would otherwise
    read as a working stamp.
    """
    if not isinstance(value, str) or len(value) != 40:
        return DEVELOPMENT
    try:
        int(value, 16)
    except ValueError:
        return DEVELOPMENT
    return value.lower()


def _parse_sequence(value: Any) -> int:
    if isinstance(value, int) and not isinstance(value, bool) and value > 0:
        return value
    if isinstance(value, str):
        try:
            parsed = int(value)
        except ValueError:
            return 0
        return parsed if parsed > 0 else 0
    return 0


def _parse_built_at(value: Any) -> datetime | None:
    if not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value)
    except ValueError:
        return None
