"""The build stamp: what it accepts, and what it refuses.

Every one of these is a failure mode that would otherwise be found in
production by an endpoint reporting ``dev`` on a pod that is very much not a
development build - and by then the question being asked is which commit is
running, which is exactly the question that has stopped being answerable.
"""

from datetime import datetime

import aerie_trading
from aerie_trading.revision import DEVELOPMENT, read_revision, revision_from_mapping

SHA = "0123456789abcdef0123456789abcdef01234567"


def test_an_unstamped_build_reports_development() -> None:
    # No build.json is committed - it is written into the package by
    # Dockerfile.trading - so this is the real answer in a working tree.
    revision = read_revision()

    assert revision.revision == DEVELOPMENT
    assert revision.sequence == 0
    assert revision.is_development


def test_a_full_stamp_is_read_back() -> None:
    revision = revision_from_mapping(
        {"revision": SHA, "sequence": 4321, "builtAt": "2026-09-02T12:00:00+00:00"}
    )

    assert revision.revision == SHA
    assert revision.sequence == 4321
    assert revision.built_at == datetime.fromisoformat("2026-09-02T12:00:00+00:00")
    assert not revision.is_development


def test_a_sha_is_lowercased() -> None:
    assert revision_from_mapping({"revision": SHA.upper()}).revision == SHA


def test_a_short_sha_is_not_a_sha() -> None:
    # The image tags carried a 7-character sha before docs/plans/version.md
    # widened them. Half-migrating to the full one has to read as unstamped
    # rather than as a working stamp of the wrong length.
    assert revision_from_mapping({"revision": SHA[:7]}).revision == DEVELOPMENT


def test_a_non_hex_revision_is_not_a_sha() -> None:
    assert revision_from_mapping({"revision": "z" * 40}).revision == DEVELOPMENT


def test_a_missing_or_malformed_stamp_never_raises() -> None:
    stamps: list[object] = [{}, {"revision": None}, [], "nonsense", 7]
    for stamp in stamps:
        assert revision_from_mapping(stamp).revision == DEVELOPMENT


def test_a_sequence_is_a_positive_integer_however_it_arrives() -> None:
    assert revision_from_mapping({"sequence": 12}).sequence == 12
    # Build tooling hands numbers around as strings more often than not.
    assert revision_from_mapping({"sequence": "12"}).sequence == 12
    assert revision_from_mapping({"sequence": 0}).sequence == 0
    assert revision_from_mapping({"sequence": -1}).sequence == 0
    assert revision_from_mapping({"sequence": "not a number"}).sequence == 0
    # True is an int in Python, and a sequence of 1 derived from a boolean
    # would be a plausible-looking lie.
    assert revision_from_mapping({"sequence": True}).sequence == 0


def test_an_unparseable_built_at_is_absent_rather_than_wrong() -> None:
    assert revision_from_mapping({"builtAt": "sometime tuesday"}).built_at is None
    assert revision_from_mapping({"builtAt": 1234}).built_at is None


def test_the_package_still_reports_its_own_version() -> None:
    # Phase 0b's smoke test, kept: it is what proves `uv sync` installed this
    # project rather than merely putting it on sys.path.
    assert aerie_trading.__version__
