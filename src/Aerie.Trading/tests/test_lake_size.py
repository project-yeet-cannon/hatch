"""How big the lake gets, measured here and extrapolated to a real watchlist.

Phase 3's last gate condition: *"lake size per session measured and
extrapolated **at the row counts a real chain implies, not the synthetic
watchlist's**, so the PVC's lifetime is a number rather than a hope."*

So the measurement is taken against a board the size a liquid underlying
actually carries - the synthetic generator is configured up to roughly six and
a half thousand contracts for it - rather than against the four-name default
universe, whose 204-contract boards would flatter the per-row figure by a
factor of three. Parquet's per-file overhead is fixed, so a small file's
bytes-per-row is dominated by its footer; the number that matters is the one a
real file converges on.

**This test decides the PVC's size**, and the two are coupled by hand:
``deploy/cluster/trading/lake/lake-pvc.yaml`` carries the capacity and cites
the figure below. The test cannot read that manifest - the silo's extraction
seam means no path under ``src/Aerie.Trading/`` may point outside it, and
``ci.yml``'s ``trading-boundary`` job enforces exactly that - so the two are
kept honest by both naming the other rather than by a check. If the assertion
at the bottom of this file starts failing, the manifest is the thing to change.
"""

from datetime import UTC, date, datetime, timedelta

import pytest

from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

SESSION_DAY = date(2026, 3, 4)
GIB = 1024**3

# -- The reference workload, which is a set of assumptions and is written as one.
#
# None of these are measurements of this installation - there is nothing to
# measure yet, which is the whole reason the plan asks for an extrapolation.
# They are the shape of a plausible production watchlist, chosen so that the
# answer errs large:

#: Two index-scale boards (an ETF on the S&P carries thousands of strikes
#: across weeklies, monthlies and LEAPS) and eight large-cap boards.
REFERENCE_BOARDS = ((2, 15_000), (8, 2_500))

#: Snapshots per session at the shipped 30-minute interval, plus the close.
SNAPSHOTS_PER_SESSION = 14

#: Sessions per year. The convention the generator already uses.
SESSIONS_PER_YEAR = 252

#: Equities getting minute *and* daily bars. Larger than the chain watchlist,
#: because bars are the cheap dataset.
BAR_SYMBOLS = 25

#: What ``deploy/cluster/trading/lake/lake-pvc.yaml`` requests. See the module
#: docstring for why this constant is duplicated rather than read.
PVC_CAPACITY_BYTES = 32 * GIB

#: The floor the PVC's declared capacity has to clear. Three years is enough
#: warning to notice the volume filling and expand it - longhorn-r2 carries
#: allowVolumeExpansion, so that is a value change - and short enough that the
#: number is not a fantasy about a watchlist nobody has configured yet.
MINIMUM_YEARS_OF_HEADROOM = 3.0


@pytest.fixture(scope="module")
def measurements(tmp_path_factory: pytest.TempPathFactory) -> dict[str, float]:
    """Bytes per row for a realistic chain snapshot and for a session of bars."""
    root = tmp_path_factory.mktemp("sizing")
    writer = LakeWriter(root, Provenance("synthetic", "a" * 40, datetime(2026, 3, 4, tzinfo=UTC)))

    # A board of roughly 6,500 contracts: 32 expiries by 121 strikes by two
    # rights. Configuration rather than code, which is Phase 2's whole point.
    big_board = SyntheticConfig(weekly_expiries=20, monthly_expiries=12, strikes_per_side=60)
    provider = SyntheticMarketDataProvider(big_board)
    session = provider.market_hours(SESSION_DAY, SESSION_DAY)[0]

    chain_report = writer.write_chain(provider.chain("ZVZZT", session.open + timedelta(minutes=30)))
    chain_bytes = (root / chain_report.partitions[0]).stat().st_size

    bar_report = writer.write_bars(
        provider.bars(
            ("ZVZZT",), Interval.ONE_MINUTE, session.open, session.close + timedelta(minutes=1)
        )
    )
    bar_bytes = (root / bar_report.partitions[0]).stat().st_size

    return {
        "chain_rows": chain_report.rows_written,
        "chain_bytes_per_row": chain_bytes / chain_report.rows_written,
        "bar_rows": bar_report.rows_written,
        "bar_bytes_per_row": bar_bytes / bar_report.rows_written,
    }


def test_the_measurement_is_taken_at_a_realistic_board_size(
    measurements: dict[str, float],
) -> None:
    """Guarding the guard.

    The extrapolation below is only worth anything if the per-row figure came
    off a file big enough for its footer to have stopped mattering. A board of
    a few hundred contracts - the default universe's - measures nearly three
    times higher per row and would size the PVC for a workload nobody runs.
    """
    assert measurements["chain_rows"] > 5_000


def test_a_row_costs_what_a_compressed_parquet_row_should(
    measurements: dict[str, float],
) -> None:
    """A regression guard on the write path, not a target.

    zstd over columns that are mostly repeated strikes, expiries and
    near-constant greeks lands around 35 bytes a row. Sixty is where this
    fails, which is roughly what turning compression off, or adding a wide
    string column to ``CHAIN_SCHEMA``, would cost - both of which should be
    decisions rather than surprises found on a full volume.
    """
    assert measurements["chain_bytes_per_row"] < 60
    assert measurements["bar_bytes_per_row"] < 60


def annual_bytes(measurements: dict[str, float]) -> float:
    contracts_per_snapshot = sum(count * size for count, size in REFERENCE_BOARDS)
    chain_rows = contracts_per_snapshot * SNAPSHOTS_PER_SESSION * SESSIONS_PER_YEAR
    # Minute bars plus one daily bar per session, per symbol.
    bar_rows = BAR_SYMBOLS * SESSIONS_PER_YEAR * (390 + 1)
    return (
        chain_rows * measurements["chain_bytes_per_row"]
        + bar_rows * measurements["bar_bytes_per_row"]
    )


def test_the_pvc_holds_more_than_three_years_of_the_reference_watchlist(
    measurements: dict[str, float], capsys: pytest.CaptureFixture[str]
) -> None:
    """Phase 3's gate: a number rather than a hope.

    The figure is printed as well as asserted, because the thing an operator
    wants from this test is the number itself - ``pytest -s`` is how the
    manifest's capacity gets re-derived when the watchlist widens.
    """
    per_year = annual_bytes(measurements)
    years = PVC_CAPACITY_BYTES / per_year

    with capsys.disabled():
        print(
            f"\n  lake growth: {per_year / GIB:.2f} GiB/year"
            f" at {measurements['chain_bytes_per_row']:.1f} bytes/contract-row"
            f"\n  PVC {PVC_CAPACITY_BYTES / GIB:.0f} GiB lasts {years:.1f} years"
        )

    assert years >= MINIMUM_YEARS_OF_HEADROOM, (
        f"the reference watchlist fills a {PVC_CAPACITY_BYTES / GIB:.0f} GiB volume in"
        f" {years:.1f} years; raise the capacity in"
        " deploy/cluster/trading/lake/lake-pvc.yaml and this file's PVC_CAPACITY_BYTES"
    )


def test_a_session_of_the_shipped_watchlist_is_small_enough_to_be_uninteresting(
    measurements: dict[str, float],
) -> None:
    """What the synthetic configuration actually costs while it is running.

    The lake fills with noise between now and Phase 8, and the PVC has to
    survive that too - but the point of this assertion is the opposite of the
    one above: it says the synthetic period is *not* what sizes the volume, so
    nobody has to hurry to turn the collectors off.
    """
    contracts = 204  # The default universe's board, measured in Phase 2.
    per_session = (
        4 * contracts * SNAPSHOTS_PER_SESSION * measurements["chain_bytes_per_row"]
        + 5 * 391 * measurements["bar_bytes_per_row"]
    )
    assert per_session * SESSIONS_PER_YEAR < GIB
