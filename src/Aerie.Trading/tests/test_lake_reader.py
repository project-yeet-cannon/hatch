"""The DuckDB reader: a symbol and a range in, a polars frame out.

Phase 3's gate asks that "a DuckDB query returns a chain snapshot as a polars
frame in reasonable time". The timing assertion here exists to catch a change
that makes a read scan the whole lake rather than to be a benchmark, so its
bound is loose and its inputs are deterministic.

The most valuable test in the file is the timezone one. DuckDB renders
``TIMESTAMP WITH TIME ZONE`` in its *session* zone, which defaults to the
host's - so without the ``SET TimeZone='UTC'`` in the reader, the same query
returns ``datetime[us, America/New_York]`` on a laptop and
``datetime[us, UTC]`` on a cluster node. That is a green CI lane over a bug.
"""

import os
import time
from collections.abc import Generator
from datetime import UTC, date, datetime, timedelta
from pathlib import Path

import polars as pl
import pytest

from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import BAR_SCHEMA, CHAIN_SCHEMA, Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

SESSION_DAY = date(2026, 3, 4)


@pytest.fixture(scope="module")
def provider() -> SyntheticMarketDataProvider:
    return SyntheticMarketDataProvider()


def build_lake(root: Path, provider: SyntheticMarketDataProvider) -> Path:
    """A week of minute bars and one session of chain snapshots."""
    writer = LakeWriter(
        root=root,
        provenance=Provenance("synthetic", "a" * 40, datetime(2026, 3, 6, 21, tzinfo=UTC)),
    )
    for session in provider.market_hours(date(2026, 3, 2), date(2026, 3, 6)):
        writer.write_bars(
            provider.bars(
                ("ZVZZT", "ZWZZT"),
                Interval.ONE_MINUTE,
                session.open,
                session.close + timedelta(minutes=1),
            )
        )
    session = provider.market_hours(SESSION_DAY, SESSION_DAY)[0]
    for offset in (0, 30, 60):
        writer.write_chain(provider.chain("ZVZZT", session.open + timedelta(minutes=offset)))
    return root


# Module-scoped, because building it is a week of minute bars for two symbols
# and every test below reads the same lake without changing it. The one test
# that does add a file to the tree takes its own copy - see the bottom of the
# file - so the sharing stays a performance decision rather than an ordering
# dependency.
@pytest.fixture(scope="module")
def lake(tmp_path_factory: pytest.TempPathFactory, provider: SyntheticMarketDataProvider) -> Path:
    return build_lake(tmp_path_factory.mktemp("lake"), provider)


@pytest.fixture(scope="module")
def reader(lake: Path) -> Generator[LakeReader]:
    with LakeReader(lake) as opened:
        yield opened


def test_bars_come_back_as_utc_whatever_the_host_thinks_the_time_is(
    lake: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """The finding this module's docstring is about.

    ``TZ`` is set to a zone that is not UTC and does not equal the CI runner's,
    so a reader that let DuckDB pick the session zone would hand back a frame
    whose dtype names Kolkata - and every dtype assertion elsewhere in this
    suite would still pass on a runner that happens to be UTC.
    """
    monkeypatch.setenv("TZ", "Asia/Kolkata")
    if hasattr(time, "tzset"):
        time.tzset()

    with LakeReader(lake) as reader:
        frame = reader.bars(
            ("ZVZZT",),
            Interval.ONE_MINUTE,
            datetime(2026, 3, 4, tzinfo=UTC),
            datetime(2026, 3, 5, tzinfo=UTC),
        )

    assert frame.schema["timestamp"] == pl.Datetime("us", "UTC")
    assert frame.get_column("timestamp")[0] == datetime(2026, 3, 4, 14, 30, tzinfo=UTC)


def test_a_read_returns_the_declared_schema_even_when_it_finds_nothing(
    reader: LakeReader,
) -> None:
    """An empty answer with the right columns, not an exception.

    DuckDB raises ``IO Error: No files found`` on a glob that matches nothing,
    which would make an un-collected range and a mistyped symbol the same
    failure. The reader names its files instead, so this is a legitimate empty.
    """
    frame = reader.bars(
        ("ZVZZT",),
        Interval.ONE_MINUTE,
        datetime(2019, 1, 2, tzinfo=UTC),
        datetime(2019, 1, 3, tzinfo=UTC),
    )
    assert frame.is_empty()
    assert frame.schema == pl.Schema(BAR_SCHEMA)


def test_a_read_is_half_open_at_the_end(reader: LakeReader) -> None:
    """Consecutive windows tile without either overlapping or leaving a hole."""
    open_at = datetime(2026, 3, 4, 14, 30, tzinfo=UTC)
    first = reader.bars(("ZVZZT",), Interval.ONE_MINUTE, open_at, open_at + timedelta(minutes=10))
    second = reader.bars(
        ("ZVZZT",),
        Interval.ONE_MINUTE,
        open_at + timedelta(minutes=10),
        open_at + timedelta(minutes=20),
    )
    assert first.height == second.height == 10
    stamps = set(first.get_column("timestamp").to_list())
    assert stamps.isdisjoint(second.get_column("timestamp").to_list())


def test_a_read_spanning_symbols_is_ordered_and_self_describing(reader: LakeReader) -> None:
    """The partition keys are columns, so a multi-symbol frame is separable
    without anyone consulting the directory the rows came out of."""
    frame = reader.bars(
        ("ZVZZT", "ZWZZT"),
        Interval.ONE_MINUTE,
        datetime(2026, 3, 4, tzinfo=UTC),
        datetime(2026, 3, 5, tzinfo=UTC),
    )
    assert frame.get_column("timestamp").is_sorted()
    assert sorted(frame.get_column("symbol").unique().to_list()) == ["ZVZZT", "ZWZZT"]
    assert frame.height == 780


def test_a_read_spanning_months_stitches_the_partitions_together(
    tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    writer = LakeWriter(tmp_path, Provenance("synthetic", "dev"))
    for session in provider.market_hours(date(2026, 1, 26), date(2026, 2, 6)):
        writer.write_bars(provider.bars(("ZVZZT",), Interval.ONE_DAY, session.open, session.close))

    with LakeReader(tmp_path) as reader:
        frame = reader.bars(
            ("ZVZZT",),
            Interval.ONE_DAY,
            datetime(2026, 1, 1, tzinfo=UTC),
            datetime(2026, 3, 1, tzinfo=UTC),
        )
    assert frame.height == 10
    assert frame.get_column("timestamp").is_sorted()


def test_a_chain_snapshot_comes_back_as_a_frame(reader: LakeReader) -> None:
    frame = reader.chain_snapshot("ZVZZT", datetime(2026, 3, 4, 15, 0, tzinfo=UTC))
    assert not frame.is_empty()
    assert frame.schema == pl.Schema(CHAIN_SCHEMA)
    assert frame.get_column("spot").n_unique() == 1
    assert set(frame.get_column("option_right").unique().to_list()) == {"call", "put"}


def test_a_snapshot_that_was_never_taken_is_empty_rather_than_the_nearest_one(
    reader: LakeReader,
) -> None:
    """Exact, not nearest.

    Handing back the 15:00 board for a 15:07 request is how a backtest prices a
    position against a board taken seven minutes earlier with nothing in the
    record saying so.
    """
    frame = reader.chain_snapshot("ZVZZT", datetime(2026, 3, 4, 15, 7, tzinfo=UTC))
    assert frame.is_empty()
    assert frame.schema == pl.Schema(CHAIN_SCHEMA)


def test_the_snapshots_taken_on_a_day_are_readable_without_opening_them(
    reader: LakeReader,
) -> None:
    """Read from the file names, which is what the layout puts them there for."""
    assert reader.snapshot_times("ZVZZT", SESSION_DAY) == (
        datetime(2026, 3, 4, 14, 30, tzinfo=UTC),
        datetime(2026, 3, 4, 15, 0, tzinfo=UTC),
        datetime(2026, 3, 4, 15, 30, tzinfo=UTC),
    )


def test_several_snapshots_read_as_one_frame_separable_by_timestamp(
    reader: LakeReader,
) -> None:
    frame = reader.chain_snapshots(
        "ZVZZT",
        datetime(2026, 3, 4, tzinfo=UTC),
        datetime(2026, 3, 5, tzinfo=UTC),
    )
    assert frame.get_column("timestamp").n_unique() == 3
    assert frame.get_column("timestamp").is_sorted()


def test_coverage_reports_bars_per_symbol_per_session(reader: LakeReader) -> None:
    """The shape a gap check wants: what the lake actually holds, per day."""
    coverage = reader.bar_coverage(
        ("ZVZZT", "ZWZZT"),
        Interval.ONE_MINUTE,
        datetime(2026, 3, 2, tzinfo=UTC),
        datetime(2026, 3, 7, tzinfo=UTC),
    )
    assert coverage.height == 10
    assert coverage.get_column("bars").unique().to_list() == [390]


def test_a_symbol_that_could_escape_the_root_is_refused_on_the_read_side_too(
    reader: LakeReader,
) -> None:
    with pytest.raises(ValueError, match="not a usable symbol"):
        reader.bars(
            ("ZVZZT/../../etc",),
            Interval.ONE_MINUTE,
            datetime(2026, 3, 4, tzinfo=UTC),
            datetime(2026, 3, 5, tzinfo=UTC),
        )


def test_a_chain_snapshot_query_returns_in_reasonable_time(reader: LakeReader) -> None:
    """Phase 3's gate. A bound to catch a scan of the whole lake, not a benchmark.

    Measured at roughly 5 ms on a laptop; the bound is two hundred times that,
    because what it exists to catch is a change that turns a named-file read
    into a directory walk - which is orders of magnitude, not percentages.
    """
    started = time.perf_counter()
    frame = reader.chain_snapshot("ZVZZT", datetime(2026, 3, 4, 15, 0, tzinfo=UTC))
    elapsed = time.perf_counter() - started

    assert not frame.is_empty()
    assert elapsed < 1.0, f"a single chain snapshot took {elapsed:.3f}s"


def test_a_reader_opens_no_files_it_was_not_asked_for(
    tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    """The file list is computed from the layout, so a stray file in the tree
    is not swept into a read. Here that stray is a Parquet file with an
    incompatible schema - which a glob-based reader would fail on.

    On its own lake rather than the module's, because it is the one test here
    that writes into the tree.
    """
    root = build_lake(tmp_path, provider)
    stray = root / "bars" / "1m" / "ZVZZT" / "2026" / "backup.parquet"
    pl.DataFrame({"nonsense": [1]}).write_parquet(stray)

    with LakeReader(root) as reader:
        frame = reader.bars(
            ("ZVZZT",),
            Interval.ONE_MINUTE,
            datetime(2026, 3, 4, tzinfo=UTC),
            datetime(2026, 3, 5, tzinfo=UTC),
        )
    assert frame.height == 390
    assert os.path.exists(stray)
