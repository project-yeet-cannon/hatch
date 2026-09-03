"""Option boards: structurally valid, numerically meaningless.

docs/plans/trading.md Phase 2 asks for "a plausible strike ladder and expiry
calendar, the full row shape Phase 3's partition layout expects, and noise in
every price, greek and IV field", and is equally explicit about what that is
for: it is enough to exercise the collector, the partition scheme, the DuckDB
reader and the health metrics - the plumbing - and it is enough for nothing
else.

**Every number in a contract row here is noise.** The greeks are not derived
from the prices, the prices are not derived from the volatility, and the
volatility is not derived from anything. A delta of 0.62 on one strike and 0.31
on the next does not mean the first is further in the money; the two are
independent draws that happen to be adjacent in a ladder. Reading these as
prices produces a confident wrong answer, which is why
``require_priceable_chains`` exists and why this source reports
``chains_are_priceable = False``.

**What *is* honest here is the structure**, and only because a collector that
tripped over it should be tripping over real data rather than over the fixture:

- the strike ladder uses the increment the price level implies, and is centred
  on the money;
- expiries are weekly and monthly Fridays that fall back to the previous
  session when the Friday is a holiday, which is what the exchanges do;
- the OCC contract symbol is spelled the way the OCC spells it, so a consumer
  that parses one is parsing the real format;
- bid never exceeds ask, a price is never below intrinsic value, gamma and vega
  are non-negative, theta is non-positive, and delta has the sign and range its
  right implies.

The intrinsic-value floor is the one that looks like modelling and is not. It
is ``max(0, spot - strike)`` on two numbers already in the row - arithmetic,
not a pricing model - and without it the board carries contracts trading below
their own exercise value, which is a shape no venue produces and which a
consumer is entitled to assume away.
"""

from __future__ import annotations

from datetime import date, datetime, timedelta

from aerie_trading.providers.base import ChainSnapshot, OptionQuote, OptionRight
from aerie_trading.providers.calendar import MarketCalendar
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.noise import NoiseStream

__all__ = ["OPTION_MULTIPLIER", "build_chain", "expiry_ladder", "occ_symbol", "strike_ladder"]

#: Shares per contract for a standard US listed option. The Ledger stores a
#: multiplier per instrument rather than assuming this one, because an adjusted
#: contract after a split has neither 100 nor any other constant - but the
#: generator produces no corporate actions, so every contract it lists is
#: standard.
OPTION_MULTIPLIER = 100

#: Strike increments by price level, in dollars. Roughly what the listed
#: markets use, and here for one reason: a ladder with a fixed increment is
#: either absurdly sparse on a $400 name or absurdly dense on a $7 one, and
#: both are shapes that hide a bug in a consumer that assumes a strike count.
_STRIKE_INCREMENTS: tuple[tuple[float, float], ...] = (
    (25.0, 0.50),
    (100.0, 1.00),
    (200.0, 2.50),
    (float("inf"), 5.00),
)


def strike_increment(spot: float) -> float:
    for ceiling, increment in _STRIKE_INCREMENTS:
        if spot < ceiling:
            return increment
    return _STRIKE_INCREMENTS[-1][1]


def strike_ladder(spot: float, strikes_per_side: int) -> tuple[float, ...]:
    """A symmetric ladder of strikes around the money.

    Centred on the increment nearest the spot rather than on the spot itself,
    so the ladder sits on the round numbers a board actually lists. Strikes are
    generated in whole cents and returned as floats rounded to two places: the
    Ledger stores a strike as ``Numeric(12, 4)`` and a strike is the one number
    in a contract's identity, so a value that cannot round-trip through a
    decimal is a contract that cannot be looked up by the identity it was
    written with.
    """
    increment = strike_increment(spot)
    centre = round(spot / increment) * increment
    ladder = [
        round(centre + step * increment, 2)
        for step in range(-strikes_per_side, strikes_per_side + 1)
    ]
    return tuple(strike for strike in ladder if strike > 0)


def _friday_of(week_of: date) -> date:
    """The Friday of ``week_of``'s week, Monday-based."""
    return week_of + timedelta(days=(4 - week_of.weekday()))


def _third_friday(year: int, month: int) -> date:
    first = date(year, month, 1)
    first_friday = first + timedelta(days=(4 - first.weekday()) % 7)
    return first_friday + timedelta(days=14)


def expiry_ladder(
    as_of: date,
    calendar: MarketCalendar,
    weeklies: int,
    monthlies: int,
) -> tuple[date, ...]:
    """The expiries a board carries on ``as_of``: weekly then monthly Fridays.

    A Friday that is not a session moves the expiry back to the previous one -
    Good Friday is the case everyone hits eventually - rather than skipping the
    week. Monthlies are third Fridays, and any that collide with a weekly are
    folded together rather than listed twice, because a board does not carry
    the same expiry under two names.
    """
    candidates: list[date] = []
    week = _friday_of(as_of)
    if week <= as_of:
        week += timedelta(days=7)
    for offset in range(weeklies):
        candidates.append(week + timedelta(days=7 * offset))

    year, month = as_of.year, as_of.month
    for _ in range(monthlies):
        third = _third_friday(year, month)
        if third > as_of:
            candidates.append(third)
        month += 1
        if month > 12:
            year, month = year + 1, 1

    resolved: set[date] = set()
    for candidate in candidates:
        session = calendar.session_on_or_before(candidate)
        if session.session > as_of:
            resolved.add(session.session)
    return tuple(sorted(resolved))


def occ_symbol(underlying: str, expiry: date, right: OptionRight, strike: float) -> str:
    """The OCC 21-character contract symbol.

    Root padded to six, ``YYMMDD``, ``C`` or ``P``, then the strike in
    thousandths across eight digits. Spelled the way the OCC spells it because
    a consumer that parses one of these is going to be handed a real one at
    Phase 8, and a fixture in a private format would have taught it the wrong
    parser.
    """
    letter = "C" if right is OptionRight.CALL else "P"
    thousandths = round(strike * 1000)
    return f"{underlying.upper():<6}{expiry:%y%m%d}{letter}{thousandths:08d}"


def _contract(
    underlying: str,
    spot: float,
    expiry: date,
    strike: float,
    right: OptionRight,
    as_of: datetime,
    stream: NoiseStream,
) -> OptionQuote:
    """One contract row. Every number below the identity fields is a draw.

    Each field has its own sub-stream, keyed by the contract's identity, so
    that the value in a field depends on which contract and which field it is
    and on nothing else - adding a field later does not shift the ones beside
    it, and a board generated for one expiry is identical to the same expiry
    inside a board generated for ten.
    """
    contract = stream.derive(expiry.isoformat(), right.value, f"{strike:.2f}")

    intrinsic = max(0.0, spot - strike) if right is OptionRight.CALL else max(0.0, strike - spot)
    # Time value as a fraction of spot, from a draw. Not a price, and not
    # trying to be: it is the padding that keeps the row above intrinsic.
    time_value = spot * 0.02 * contract.uniform(0)
    mid = max(intrinsic + time_value, 0.01)

    half_spread = max(0.01, mid * 0.02 * (0.5 + contract.uniform(1))) / 2
    bid = max(round(mid - half_spread, 2), 0.0)
    ask = max(round(mid + half_spread, 2), bid + 0.01)
    # The last trade sits inside the spread, which is where a print sits.
    last = round(bid + (ask - bid) * contract.uniform(2), 2)

    # Delta carries the sign its right implies and stays inside its range; the
    # magnitude is noise. Sign and range are structure, magnitude is not.
    magnitude = contract.uniform(3)
    delta = magnitude if right is OptionRight.CALL else magnitude - 1.0

    return OptionQuote(
        symbol=occ_symbol(underlying, expiry, right, strike),
        underlying=underlying,
        expiry=expiry,
        strike=strike,
        right=right,
        timestamp=as_of,
        bid=bid,
        ask=ask,
        last=last,
        volume=contract.integer(4, 0, 5_000),
        open_interest=contract.integer(5, 0, 50_000),
        # Clamped into a range a surface could plausibly occupy. There is no
        # surface here - two adjacent strikes are independent draws - but a
        # consumer that divides by an IV of zero deserves better than a
        # fixture that hands it one.
        implied_volatility=round(0.08 + 1.2 * contract.uniform(6), 4),
        delta=round(delta, 4),
        gamma=round(0.25 * contract.uniform(7), 4),
        theta=round(-2.0 * contract.uniform(8), 4),
        vega=round(0.6 * contract.uniform(9), 4),
        rho=round((0.4 * contract.uniform(10)) * (1.0 if right is OptionRight.CALL else -1.0), 4),
        multiplier=OPTION_MULTIPLIER,
    )


def build_chain(
    config: SyntheticConfig,
    calendar: MarketCalendar,
    underlying: str,
    as_of: datetime,
    spot: float,
    expiries: tuple[date, ...] | None = None,
) -> ChainSnapshot:
    """The whole board on one underlying at one instant.

    Ordered by expiry, then strike, then right, so that two snapshots of the
    same instant are comparable without sorting - the same property
    ``bars()`` has, and for the same reason: Phase 3's idempotency gate
    re-runs a collection and compares.
    """
    board = expiries
    if board is None:
        board = expiry_ladder(
            as_of.date(), calendar, config.weekly_expiries, config.monthly_expiries
        )
    ladder = strike_ladder(spot, config.strikes_per_side)
    stream = NoiseStream.named(config.seed, "synthetic", underlying, "chain")

    contracts = tuple(
        _contract(underlying, spot, expiry, strike, right, as_of, stream)
        for expiry in sorted(board)
        for strike in ladder
        for right in (OptionRight.CALL, OptionRight.PUT)
    )
    return ChainSnapshot(
        underlying=underlying,
        timestamp=as_of,
        spot=spot,
        contracts=contracts,
    )
