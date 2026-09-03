"""The synthetic market, and the five things Phase 2's gate asks of it.

docs/plans/trading.md Phase 2 gate, in order: bars and chains are produced for
a configured universe; two identical requests return byte-identical data; a
full simulated session generates in CI in seconds with no network; generated
returns show no exploitable autocorrelation; and the calendar refuses to
generate a session on a market holiday.

The fourth lives in ``test_synthetic_zero_alpha.py``, because it is a
measurement rather than an assertion about an interface. The rest are here,
along with the structural properties that keep the source honest about what it
is - the shape is real, the numbers are noise, and the code refuses to let
anyone mistake the second for the first.
"""

import time
from datetime import UTC, date, datetime

import pytest

from aerie_trading.providers.base import (
    CHAINS_ARE_PRICEABLE,
    Interval,
    MarketClosed,
    OptionRight,
    OptionsNotPriceable,
    chains_are_priceable_in,
    require_priceable_chains,
)
from aerie_trading.providers.synthetic.chains import occ_symbol, strike_ladder
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

# A Tuesday, mid-session, well inside the calendar. Fixed rather than derived
# from today, because a test whose subject is a market calendar should not have
# a market calendar in its own setup.
SESSION = date(2026, 9, 1)
MIDDAY = datetime(2026, 9, 1, 15, 0, tzinfo=UTC)
HOLIDAY_MIDDAY = datetime(2026, 7, 3, 15, 0, tzinfo=UTC)


@pytest.fixture(scope="module")
def provider() -> SyntheticMarketDataProvider:
    return SyntheticMarketDataProvider()


# -- the universe -----------------------------------------------------------


def test_bars_are_produced_for_every_symbol_in_the_universe(
    provider: SyntheticMarketDataProvider,
) -> None:
    bars = provider.bars(
        provider.config.symbols,
        Interval.ONE_DAY,
        datetime(2026, 8, 1, tzinfo=UTC),
        datetime(2026, 9, 1, tzinfo=UTC),
    )

    assert {bar.symbol for bar in bars} == set(provider.config.symbols)
    # Ordered by timestamp then symbol, so that two collections of the same
    # window are comparable without sorting - which is the property Phase 3's
    # idempotency gate leans on.
    assert list(bars) == sorted(bars, key=lambda bar: (bar.timestamp, bar.symbol))


def test_a_symbol_outside_the_universe_is_reported_rather_than_invented(
    provider: SyntheticMarketDataProvider,
) -> None:
    # The generator could produce a series for any string. Doing so would
    # answer a typo instead of reporting it, which is Phase 3's "recording
    # silence as data" one layer up.
    with pytest.raises(KeyError):
        provider.quotes(["NOTREAL"], MIDDAY)


def test_chains_are_produced_and_a_symbol_without_a_board_says_so(
    provider: SyntheticMarketDataProvider,
) -> None:
    snapshot = provider.chain("ZVZZT", MIDDAY)
    assert snapshot.contracts
    assert snapshot.underlying == "ZVZZT"

    # ZJZZT carries no options, so that "this symbol has no chain" is a case
    # Phase 3's collector meets here rather than at Phase 8.
    with pytest.raises(KeyError):
        provider.chain("ZJZZT", MIDDAY)


# -- determinism ------------------------------------------------------------


def test_two_identical_requests_return_identical_data(
    provider: SyntheticMarketDataProvider,
) -> None:
    window = (
        datetime(2026, 8, 3, tzinfo=UTC),
        datetime(2026, 8, 8, tzinfo=UTC),
    )
    first = provider.bars(["ZVZZT"], Interval.FIVE_MINUTE, *window)
    second = provider.bars(["ZVZZT"], Interval.FIVE_MINUTE, *window)

    assert first == second
    assert provider.chain("ZVZZT", MIDDAY) == provider.chain("ZVZZT", MIDDAY)
    assert provider.quotes(["ZVZZT"], MIDDAY) == provider.quotes(["ZVZZT"], MIDDAY)


def test_a_bar_does_not_depend_on_the_window_it_was_asked_for(
    provider: SyntheticMarketDataProvider,
) -> None:
    # This is the property Phase 3's idempotency gate is actually testing: a
    # backfill of a month and an incremental collection of one day inside it
    # must agree, or a re-run writes different numbers over the same partition.
    month = provider.bars(
        ["ZVZZT"],
        Interval.ONE_DAY,
        datetime(2026, 8, 1, tzinfo=UTC),
        datetime(2026, 9, 1, tzinfo=UTC),
    )
    single = provider.bars(
        ["ZVZZT"],
        Interval.ONE_DAY,
        datetime(2026, 8, 12, tzinfo=UTC),
        datetime(2026, 8, 13, tzinfo=UTC),
    )

    assert len(single) == 1
    assert single[0] in month


def test_a_second_provider_instance_agrees_with_the_first() -> None:
    # A fresh process, in effect. The generator holds no path and no cursor, so
    # the collector pod and the worker pod that read the same day get the same
    # numbers - which is what "stateless and deterministic" has to mean to be
    # worth anything.
    window = (
        datetime(2026, 8, 3, tzinfo=UTC),
        datetime(2026, 8, 4, tzinfo=UTC),
    )
    assert SyntheticMarketDataProvider().bars(
        ["ZWZZT"], Interval.ONE_MINUTE, *window
    ) == SyntheticMarketDataProvider().bars(["ZWZZT"], Interval.ONE_MINUTE, *window)


def test_a_different_seed_is_a_different_market() -> None:
    window = (
        datetime(2026, 8, 3, tzinfo=UTC),
        datetime(2026, 8, 4, tzinfo=UTC),
    )
    default = SyntheticMarketDataProvider().bars(["ZVZZT"], Interval.ONE_DAY, *window)
    reseeded = SyntheticMarketDataProvider(SyntheticConfig(seed=7)).bars(
        ["ZVZZT"], Interval.ONE_DAY, *window
    )

    assert default[0].close != reseeded[0].close


# -- speed ------------------------------------------------------------------


def test_a_full_session_generates_in_seconds_with_no_network(
    provider: SyntheticMarketDataProvider,
) -> None:
    # The gate, measured rather than asserted by inspection. A whole session of
    # minute bars for the whole universe is what Phase 3 collects on an
    # ordinary day, and the point of the synthetic source is that a collector
    # run, a backtest and a sweep all execute in seconds in CI.
    started = time.perf_counter()
    bars = provider.bars(
        provider.config.symbols,
        Interval.ONE_MINUTE,
        datetime(2026, 9, 1, tzinfo=UTC),
        datetime(2026, 9, 2, tzinfo=UTC),
    )
    elapsed = time.perf_counter() - started

    assert len(bars) == 390 * len(provider.config.symbols)
    # Generous by two orders of magnitude against the measured figure. This is
    # a guard against a change that makes the walk quadratic, not a benchmark.
    assert elapsed < 5.0


# -- shape ------------------------------------------------------------------


def test_bars_tile_the_session_and_stop_at_the_close(
    provider: SyntheticMarketDataProvider,
) -> None:
    session = provider.calendar.session_on(SESSION)
    bars = provider.bars(["ZVZZT"], Interval.THIRTY_MINUTE, session.open, session.close)

    assert len(bars) == 13
    assert bars[0].timestamp == session.open
    assert bars[-1].timestamp < session.close


def test_an_early_close_produces_a_short_session(
    provider: SyntheticMarketDataProvider,
) -> None:
    # The one piece of Phase 3 that fails silently rather than loudly. A
    # generator that ran to 16:00 regardless would leave it unexercised.
    session = provider.calendar.session_on(date(2026, 11, 27))
    bars = provider.bars(["ZVZZT"], Interval.ONE_MINUTE, session.open, session.close)

    assert len(bars) == 210


def test_no_bars_are_produced_across_a_holiday(
    provider: SyntheticMarketDataProvider,
) -> None:
    bars = provider.bars(
        ["ZVZZT"],
        Interval.ONE_DAY,
        datetime(2026, 7, 3, tzinfo=UTC),
        datetime(2026, 7, 4, tzinfo=UTC),
    )
    assert bars == ()


def test_a_quote_on_a_holiday_is_refused(provider: SyntheticMarketDataProvider) -> None:
    # Refused rather than empty: an empty answer is indistinguishable from a
    # market that was open and silent, and the difference is the whole of
    # "do not record silence as data".
    with pytest.raises(MarketClosed):
        provider.quotes(["ZVZZT"], HOLIDAY_MIDDAY)
    with pytest.raises(MarketClosed):
        provider.chain("ZVZZT", HOLIDAY_MIDDAY)


def test_the_ohlc_relationships_hold_on_every_bar(
    provider: SyntheticMarketDataProvider,
) -> None:
    # `Bar` rejects a violation in its constructor, so this is really a test
    # that the generator never asks it to - a collector or an engine that trips
    # over `high < close` should be tripping over real data, not the fixture.
    bars = provider.bars(
        provider.config.symbols,
        Interval.FIVE_MINUTE,
        datetime(2026, 8, 24, tzinfo=UTC),
        datetime(2026, 8, 29, tzinfo=UTC),
    )

    assert bars
    for bar in bars:
        assert bar.low <= min(bar.open, bar.close)
        assert bar.high >= max(bar.open, bar.close)
        assert bar.low > 0
        assert bar.volume >= 0
        # No corporate actions in this source, so the two closes agree. The
        # column exists because Phase 3's lake stores both.
        assert bar.adjusted_close == bar.close


def test_a_quote_agrees_with_the_minute_bar_that_contains_it(
    provider: SyntheticMarketDataProvider,
) -> None:
    # One series read two ways rather than two series that happen to be near
    # each other. A consumer that cross-checks a quote against a bar is doing
    # something reasonable and should not be punished for it.
    session = provider.calendar.session_on(SESSION)
    quote = provider.quotes(["ZVZZT"], MIDDAY)[0]
    minute = next(
        bar
        for bar in provider.bars(["ZVZZT"], Interval.ONE_MINUTE, session.open, session.close)
        if bar.timestamp == MIDDAY
    )

    assert quote.last == minute.close
    assert quote.bid <= quote.last <= quote.ask


# -- the option board -------------------------------------------------------


def test_the_board_has_the_shape_a_board_has(
    provider: SyntheticMarketDataProvider,
) -> None:
    config = provider.config
    snapshot = provider.chain("ZVZZT", MIDDAY)
    ladder = strike_ladder(snapshot.spot, config.strikes_per_side)

    # Four weeklies and three monthlies, less the overlap between them:
    # September's third Friday is also a weekly, and a board does not carry the
    # same expiry under two names.
    assert snapshot.expiries == tuple(sorted(set(snapshot.expiries)))
    assert date(2026, 9, 18) in snapshot.expiries
    assert (
        config.weekly_expiries
        <= len(snapshot.expiries)
        <= config.weekly_expiries + config.monthly_expiries
    )
    assert all(expiry > MIDDAY.date() for expiry in snapshot.expiries)
    assert len(snapshot.contracts) == len(snapshot.expiries) * len(ladder) * 2
    assert {contract.strike for contract in snapshot.contracts} == set(ladder)
    # Every expiry is a session: a Friday holiday moves the board back a day.
    assert all(provider.calendar.is_session(expiry) for expiry in snapshot.expiries)


def test_contract_symbols_are_spelled_the_way_the_occ_spells_them() -> None:
    # A consumer that parses one of these will be handed a real one at Phase 8,
    # and a fixture in a private format would have taught it the wrong parser.
    assert occ_symbol("ZVZZT", date(2026, 9, 18), OptionRight.CALL, 102.5) == (
        "ZVZZT 260918C00102500"
    )
    assert occ_symbol("ZVZZT", date(2026, 9, 18), OptionRight.PUT, 7.0) == ("ZVZZT 260918P00007000")


def test_every_contract_row_is_structurally_valid(
    provider: SyntheticMarketDataProvider,
) -> None:
    # Structurally valid, numerically meaningless. The assertions below are the
    # first half; there is deliberately nothing here that checks a number
    # against a model, because there is no model.
    snapshot = provider.chain("ZVZZT", MIDDAY)

    for contract in snapshot.contracts:
        intrinsic = (
            max(0.0, snapshot.spot - contract.strike)
            if contract.right is OptionRight.CALL
            else max(0.0, contract.strike - snapshot.spot)
        )
        assert contract.bid <= contract.last <= contract.ask
        assert contract.ask >= intrinsic
        assert contract.implied_volatility > 0
        assert contract.gamma >= 0
        assert contract.vega >= 0
        assert contract.theta <= 0
        assert contract.volume >= 0
        assert contract.open_interest >= 0
        assert contract.multiplier == 100
        if contract.right is OptionRight.CALL:
            assert 0 <= contract.delta <= 1
            assert contract.rho >= 0
        else:
            assert -1 <= contract.delta <= 0
            assert contract.rho <= 0


def test_narrowing_a_board_to_one_expiry_does_not_change_its_rows(
    provider: SyntheticMarketDataProvider,
) -> None:
    # Each contract's numbers are keyed by its own identity, so a board asked
    # for one expiry is a subset of the board asked for all of them. Phase 3
    # collects a watchlist on an interval and will narrow it; the collector
    # must not be the reason two snapshots disagree.
    whole = provider.chain("ZVZZT", MIDDAY)
    expiry = whole.expiries[1]
    narrowed = provider.chain("ZVZZT", MIDDAY, [expiry])

    assert narrowed.contracts == tuple(
        contract for contract in whole.contracts if contract.expiry == expiry
    )


# -- the guardrail ----------------------------------------------------------


def test_pricing_options_against_this_source_is_refused(
    provider: SyntheticMarketDataProvider,
) -> None:
    # docs/plans/trading.md Phase 2 makes this a refusal in code, the way Phase
    # 6 refuses to serve an in-sample-only figure in the serializer rather than
    # in the UI. Noise chains are fine for moving bytes and meaningless for
    # pricing anything, and the distance between those two is exactly where a
    # plausible-looking wrong answer would come from.
    assert provider.chains_are_priceable is False
    with pytest.raises(OptionsNotPriceable, match="synthetic"):
        require_priceable_chains(provider)


def test_the_refusal_survives_the_trip_through_the_lake(
    provider: SyntheticMarketDataProvider,
) -> None:
    # A backtest reads rows, not providers. All that reaches it of a provider
    # is the `data_source` row's provenance, so the claim has to be readable
    # from that alone - and absent means no, because the rows a collector wrote
    # before this key existed cannot vouch for themselves.
    assert chains_are_priceable_in(provider.provenance) is False
    assert chains_are_priceable_in({}) is False
    assert chains_are_priceable_in({CHAINS_ARE_PRICEABLE: True}) is True


def test_provenance_is_enough_to_rebuild_the_provider(
    provider: SyntheticMarketDataProvider,
) -> None:
    # "So that a run is reproducible from its provenance alone." The test of
    # that claim is rebuilding the provider from the record and getting the
    # same numbers back.
    recorded = provider.provenance["config"]
    rebuilt = SyntheticMarketDataProvider(SyntheticConfig.model_validate(recorded))

    window = (
        datetime(2026, 8, 3, tzinfo=UTC),
        datetime(2026, 8, 4, tzinfo=UTC),
    )
    assert rebuilt.bars(["ZVZZT"], Interval.ONE_DAY, *window) == provider.bars(
        ["ZVZZT"], Interval.ONE_DAY, *window
    )
