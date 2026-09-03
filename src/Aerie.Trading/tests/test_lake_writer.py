"""Idempotent writes, and surviving a kill - two of Phase 3's gate conditions.

The plan states them separately because they are separate mechanisms:

- *"A re-run over the same window overwrites its own partition and does not
  append duplicates."* - the anti-join merge.
- *"a deliberately killed mid-collection run leaves no duplicate rows on
  re-run"* - the write-to-temporary-then-rename.

Each gets its own tests, and the last test in this file kills a run in the
middle and then re-runs it, which is the gate condition verbatim.
"""

from datetime import UTC, date, datetime, timedelta
from pathlib import Path
from typing import Any

import polars as pl
import pytest

from aerie_trading.lake.layout import BarPartition
from aerie_trading.lake.schema import BAR_SCHEMA, CHAIN_SCHEMA, Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Bar, Interval, MarketSession
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

COLLECTED_AT = datetime(2026, 3, 4, 21, 30, tzinfo=UTC)
REVISION = "a" * 40


@pytest.fixture
def provider() -> SyntheticMarketDataProvider:
    return SyntheticMarketDataProvider()


@pytest.fixture
def writer(tmp_path: Path) -> LakeWriter:
    return LakeWriter(
        root=tmp_path,
        provenance=Provenance("synthetic", REVISION, COLLECTED_AT),
    )


def session_on(provider: SyntheticMarketDataProvider, day: date) -> MarketSession:
    sessions = provider.market_hours(day, day)
    assert sessions, f"{day} is not a session"
    return sessions[0]


def bars_for(
    provider: SyntheticMarketDataProvider,
    session: MarketSession,
    symbols: tuple[str, ...] = ("ZVZZT",),
    interval: Interval = Interval.ONE_MINUTE,
) -> list[Bar]:
    return list(
        provider.bars(symbols, interval, session.open, session.close + timedelta(minutes=1))
    )


def test_a_write_stamps_every_row_with_its_provenance(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    """The plan asks every file to carry the provider, the collection time and
    the revision. They are columns rather than file metadata, so they survive a
    read that concatenates twelve partitions - see ``lake/schema.py``."""
    session = session_on(provider, date(2026, 3, 4))
    report = writer.write_bars(bars_for(provider, session))

    frame = pl.read_parquet(tmp_path / report.partitions[0])
    assert frame.get_column("provider").unique().to_list() == ["synthetic"]
    assert frame.get_column("aerie_revision").unique().to_list() == [REVISION]
    assert frame.get_column("collected_at").unique().to_list() == [COLLECTED_AT]
    assert frame.schema == pl.Schema(BAR_SCHEMA)


def test_re_running_the_same_window_does_not_append_duplicates(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    session = session_on(provider, date(2026, 3, 4))
    bars = bars_for(provider, session)

    first = writer.write_bars(bars)
    second = writer.write_bars(bars)

    frame = pl.read_parquet(tmp_path / first.partitions[0])
    assert frame.height == len(bars)
    assert frame.select(("symbol", "interval", "timestamp")).is_duplicated().sum() == 0
    # Both runs report what *they* wrote, which is the same number. A report
    # that counted the partition's growth would say 0 for the second and read
    # as a failed collection.
    assert first.rows_written == second.rows_written == len(bars)


def test_a_second_session_is_added_rather_than_replacing_the_month(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    """The reason the merge is an anti-join and not a truncate.

    A bar partition is a month and a collection is a session, so overwriting
    the partition would delete twenty sessions in order to write one - and the
    incremental collector runs every weekday.
    """
    first = session_on(provider, date(2026, 3, 4))
    second = session_on(provider, date(2026, 3, 5))

    report = writer.write_bars(bars_for(provider, first))
    writer.write_bars(bars_for(provider, second))

    frame = pl.read_parquet(tmp_path / report.partitions[0])
    days = frame.get_column("timestamp").dt.date().unique().sort().to_list()
    assert days == [date(2026, 3, 4), date(2026, 3, 5)]


def test_re_collecting_one_session_replaces_only_that_session(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    """A correction to one day must not disturb the days either side of it."""
    days = [date(2026, 3, 3), date(2026, 3, 4), date(2026, 3, 5)]
    for day in days:
        writer.write_bars(bars_for(provider, session_on(provider, day)))

    partition = BarPartition(Interval.ONE_MINUTE, "ZVZZT", 2026, 3)
    before = pl.read_parquet(partition.path(tmp_path))

    # A second collection of the middle day, by a later build.
    corrected = LakeWriter(
        root=tmp_path,
        provenance=Provenance("synthetic", "b" * 40, COLLECTED_AT + timedelta(days=1)),
    )
    corrected.write_bars(bars_for(provider, session_on(provider, date(2026, 3, 4))))

    after = pl.read_parquet(partition.path(tmp_path))
    assert after.height == before.height

    # The re-collected day carries the new build's stamp; its neighbours keep
    # the old one. That is only checkable because provenance is per row.
    revisions = (
        after.with_columns(pl.col("timestamp").dt.date().alias("day"))
        .group_by("day")
        .agg(pl.col("aerie_revision").unique().first())
        .sort("day")
        .get_column("aerie_revision")
        .to_list()
    )
    assert revisions == ["a" * 40, "b" * 40, "a" * 40]


def test_a_partition_is_written_in_timestamp_order(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    """Sorted on disk is what lets a range scan skip row groups by statistics."""
    for day in (date(2026, 3, 5), date(2026, 3, 3), date(2026, 3, 4)):
        writer.write_bars(bars_for(provider, session_on(provider, day)))

    frame = pl.read_parquet(BarPartition(Interval.ONE_MINUTE, "ZVZZT", 2026, 3).path(tmp_path))
    assert frame.get_column("timestamp").is_sorted()


def test_bars_land_in_one_partition_per_symbol_per_month(
    writer: LakeWriter, provider: SyntheticMarketDataProvider
) -> None:
    session = session_on(provider, date(2026, 3, 4))
    report = writer.write_bars(bars_for(provider, session, symbols=("ZVZZT", "ZWZZT")))
    assert sorted(report.partitions) == [
        "bars/1m/ZVZZT/2026/03.parquet",
        "bars/1m/ZWZZT/2026/03.parquet",
    ]


def test_a_chain_snapshot_is_idempotent_by_path(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    session = session_on(provider, date(2026, 3, 4))
    snapshot = provider.chain("ZVZZT", session.open + timedelta(minutes=30))

    first = writer.write_chain(snapshot)
    writer.write_chain(snapshot)

    assert first.partitions == ("chains/ZVZZT/2026-03-04/1500.parquet",)
    frame = pl.read_parquet(tmp_path / first.partitions[0])
    assert frame.height == first.rows_written
    assert frame.schema == pl.Schema(CHAIN_SCHEMA)
    assert frame.select(("underlying", "symbol", "timestamp")).is_duplicated().sum() == 0


def test_an_empty_write_touches_nothing(writer: LakeWriter, tmp_path: Path) -> None:
    report = writer.write_bars(())
    assert report.rows_written == 0
    assert report.partitions == ()
    assert list(tmp_path.iterdir()) == []


# -- the kill ------------------------------------------------------------------


def test_an_interrupted_write_leaves_the_previous_file_intact(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path, monkeypatch: Any
) -> None:
    """Killed mid-write, the old partition is still readable.

    The property the rename buys. Without it the destination would be a
    truncated Parquet file whose footer never arrived, and the *re-run* would
    fail on reading it - so a single interruption would take the partition out
    permanently rather than for one run.
    """
    session = session_on(provider, date(2026, 3, 4))
    report = writer.write_bars(bars_for(provider, session))
    path = tmp_path / report.partitions[0]
    original = path.read_bytes()

    def die(*_: object, **__: object) -> None:
        raise KeyboardInterrupt("SIGINT during the write")

    monkeypatch.setattr(pl.DataFrame, "write_parquet", die)
    with pytest.raises(KeyboardInterrupt):
        writer.write_bars(bars_for(provider, session_on(provider, date(2026, 3, 5))))

    assert path.read_bytes() == original


def test_an_interrupted_write_leaves_no_temporary_files_behind(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path, monkeypatch: Any
) -> None:
    """A tree littered with ``.03.<pid>.parquet.tmp`` would be the writer's own
    mess rather than a property of the interruption - and would then be handed
    to DuckDB by any reader that globbed."""
    session = session_on(provider, date(2026, 3, 4))
    writer.write_bars(bars_for(provider, session))

    def die(*_: object, **__: object) -> None:
        raise KeyboardInterrupt("SIGINT during the write")

    monkeypatch.setattr(pl.DataFrame, "write_parquet", die)
    with pytest.raises(KeyboardInterrupt):
        writer.write_bars(bars_for(provider, session_on(provider, date(2026, 3, 5))))

    assert [path.name for path in tmp_path.rglob("*") if path.is_file()] == ["03.parquet"]


def test_a_run_killed_mid_collection_leaves_no_duplicates_on_re_run(
    writer: LakeWriter, provider: SyntheticMarketDataProvider, tmp_path: Path, monkeypatch: Any
) -> None:
    """Phase 3's gate condition, verbatim.

    A collection of four symbols is killed while writing the third, and then
    re-run from the beginning. The first two partitions were already complete
    and are re-collected; the third was mid-write and its rename never
    happened; the fourth had not started. Afterwards every partition holds
    exactly one copy of every bar.
    """
    session = session_on(provider, date(2026, 3, 4))
    symbols = ("ZVZZT", "ZWZZT", "ZXZZT", "ZBZZT")
    bars = bars_for(provider, session, symbols=symbols)

    real_write = pl.DataFrame.write_parquet
    calls = {"n": 0}

    def die_on_the_third(self: pl.DataFrame, *args: Any, **kwargs: Any) -> None:
        calls["n"] += 1
        if calls["n"] == 3:
            raise KeyboardInterrupt("the pod was evicted")
        real_write(self, *args, **kwargs)

    monkeypatch.setattr(pl.DataFrame, "write_parquet", die_on_the_third)
    with pytest.raises(KeyboardInterrupt):
        writer.write_bars(bars)

    monkeypatch.setattr(pl.DataFrame, "write_parquet", real_write)
    writer.write_bars(bars)

    total = 0
    for symbol in symbols:
        path = BarPartition(Interval.ONE_MINUTE, symbol, 2026, 3).path(tmp_path)
        frame = pl.read_parquet(path)
        assert frame.select(("symbol", "interval", "timestamp")).is_duplicated().sum() == 0
        total += frame.height
    assert total == len(bars)
