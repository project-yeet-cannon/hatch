"""The partition layout, which is the thing Phase 3 asks be fixed before data.

Every assertion here is about a path, and paths are the part of the lake that
cannot be changed later without a migration - so these tests are less about
catching a bug than about making a change to the layout impossible to make
accidentally.
"""

from datetime import UTC, date, datetime
from pathlib import Path

import pytest

from aerie_trading.lake.layout import (
    BarPartition,
    bar_partition,
    bar_partitions,
    chain_partition,
    chain_snapshot_dir,
    chain_snapshots_on,
    normalise_symbol,
    snapshot_slot,
)
from aerie_trading.providers.base import Interval

ROOT = Path("/lake")


def test_a_bar_path_is_the_layout_the_plan_names() -> None:
    path = bar_partition(
        ROOT, Interval.ONE_MINUTE, "ZVZZT", datetime(2026, 3, 4, 14, 30, tzinfo=UTC)
    )
    assert path == ROOT / "bars" / "1m" / "ZVZZT" / "2026" / "03.parquet"


def test_a_chain_path_is_the_layout_the_plan_names() -> None:
    path = chain_partition(ROOT, "ZVZZT", datetime(2026, 3, 4, 15, 30, tzinfo=UTC))
    assert path == ROOT / "chains" / "ZVZZT" / "2026-03-04" / "1530.parquet"


def test_the_month_is_taken_in_utc_not_in_the_local_zone() -> None:
    """A UTC instant late on the last of the month belongs to that month.

    The failure this guards is a partition boundary computed in local time: a
    bar at 23:30 UTC on 31 March is 19:30 Eastern on 31 March, and both agree.
    One computed in a zone *ahead* of UTC would file it under April, and the
    March file would be short by a session that nothing reports missing.
    """
    partition = BarPartition.containing(
        Interval.ONE_DAY, "ZVZZT", datetime(2026, 3, 31, 23, 59, tzinfo=UTC)
    )
    assert (partition.year, partition.month) == (2026, 3)


def test_a_naive_datetime_is_refused_rather_than_assumed() -> None:
    with pytest.raises(ValueError, match="naive"):
        bar_partition(ROOT, Interval.ONE_DAY, "ZVZZT", datetime(2026, 3, 4, 14, 30))


# Spelled with the traversal *inside* the string rather than at its start -
# `ZVZZT/../..` rather than `../..` - and that is not squeamishness. ci.yml's
# `trading-boundary` job greps for a quote immediately followed by `../`,
# because a path literal that leaves this directory is the mechanism it exists
# to catch; a rejected input in a test is not that, and a guard with an
# exception carved into it for the test directory would be a guard that no
# longer covers the test directory. These inputs are also the stronger attack:
# `Path(root) / "ZVZZT/../../etc"` escapes exactly as effectively, and does it
# past a naive check that only looked at the first characters.
@pytest.mark.parametrize(
    "symbol",
    [
        "ZVZZT/../../etc/passwd",
        "/etc/passwd",
        "ZV/ZZT",
        "ZV\\ZZT",
        "..",
        ".hidden",
        "",
        "   ",
        "A" * 33,
        "ZVZZT;DROP",
    ],
)
def test_a_symbol_that_could_escape_the_root_is_refused(symbol: str) -> None:
    """Not sanitised, refused.

    A symbol reaches the layout from a configured watchlist or from a
    provider's response, so it is not this process's own string. Stripping the
    offending characters would turn a typo into a partition that looks
    legitimate and holds another instrument's data.
    """
    with pytest.raises(ValueError, match="not a usable symbol"):
        normalise_symbol(symbol)


@pytest.mark.parametrize("symbol", ["ZVZZT", "brk.b", "spy", "ZJZZT", "RDS-A"])
def test_a_real_ticker_is_accepted_and_upper_cased(symbol: str) -> None:
    assert normalise_symbol(symbol) == symbol.strip().upper()


def test_partition_enumeration_spans_every_month_a_window_touches() -> None:
    partitions = bar_partitions(
        Interval.ONE_DAY,
        ("ZVZZT",),
        datetime(2025, 11, 15, tzinfo=UTC),
        datetime(2026, 2, 3, tzinfo=UTC),
    )
    assert [(p.year, p.month) for p in partitions] == [
        (2025, 11),
        (2025, 12),
        (2026, 1),
        (2026, 2),
    ]


def test_a_window_ending_exactly_on_a_month_boundary_excludes_that_month() -> None:
    """The half-open end, expressed in partitions.

    A read of "all of January" ends at midnight on 1 February. No row in the
    window can be in February's file, so opening it would be an IO for a
    guaranteed-empty result on every monthly read anyone ever does.
    """
    partitions = bar_partitions(
        Interval.ONE_DAY,
        ("ZVZZT",),
        datetime(2026, 1, 1, tzinfo=UTC),
        datetime(2026, 2, 1, tzinfo=UTC),
    )
    assert [(p.year, p.month) for p in partitions] == [(2026, 1)]


def test_partition_enumeration_deduplicates_symbols() -> None:
    partitions = bar_partitions(
        Interval.ONE_DAY,
        ("ZVZZT", "zvzzt", "ZVZZT"),
        datetime(2026, 1, 5, tzinfo=UTC),
        datetime(2026, 1, 6, tzinfo=UTC),
    )
    assert len(partitions) == 1


def test_snapshot_slots_are_minute_resolution_in_utc() -> None:
    assert snapshot_slot(datetime(2026, 3, 4, 9, 5, 59, tzinfo=UTC)) == "0905"


def test_listing_snapshots_for_a_day_nothing_collected_is_empty_not_an_error(
    tmp_path: Path,
) -> None:
    """An un-collected day is empty; a refusal belongs to the collector.

    A reader that raised here would make "nobody collected this day" and "you
    typed the symbol wrong" the same exception, which is precisely the
    conflation the plan's silence-as-data rule is about - solved at the layer
    that knows the difference rather than at this one.
    """
    assert chain_snapshots_on(tmp_path, "ZVZZT", date(2026, 3, 4)) == ()


def test_only_files_matching_the_slot_pattern_are_listed(tmp_path: Path) -> None:
    """A writer's leftover temporary file is not a snapshot.

    ``writer.py`` writes ``.1530.<pid>.parquet.tmp`` beside the destination and
    renames it into place. A crash between those two leaves the temporary
    behind, and a listing that globbed ``*.parquet`` would hand it to DuckDB as
    though it were a partition.
    """
    directory = chain_snapshot_dir(tmp_path, "ZVZZT", date(2026, 3, 4))
    directory.mkdir(parents=True)
    (directory / "1530.parquet").write_bytes(b"")
    (directory / ".1530.99.parquet.tmp").write_bytes(b"")
    (directory / "notes.txt").write_bytes(b"")

    assert [path.name for path in chain_snapshots_on(tmp_path, "ZVZZT", date(2026, 3, 4))] == [
        "1530.parquet"
    ]
