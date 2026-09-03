"""The named demo sweep, defined once in code rather than typed at first boot.

docs/plans/trading.md Phase 5: *"A named demo sweep, defined here and enqueued
by Phase 7's seed job: ma_crossover over a small grid of its two windows, on
one synthetic symbol, over a fixed window. Small enough to finish on a cold
cluster in minutes, large enough that the leaderboard has something to sort.
Sizing it is a decision made once, in code, rather than a number an operator
has to guess at first boot."*

**Two sweeps, not one.** The plan names the crossover grid; the baseline beside
it is added here and the reason is the plan's own Phase 6, which requires
*"baselines, computed over the identical window and shown next to every result:
buy-and-hold on the underlying"*. A seeded leaderboard holding nineteen
crossovers and nothing to compare them against would demonstrate the machinery
while withholding the one number that says whether any of it was worth doing -
on the app's own front page, which is exactly where the plan says the vertical
must arrive demonstrating itself. It costs one extra run.

``buy_and_hold`` has no swept parameters, so its "sweep" is an empty grid, which
expands to exactly one run at every default. That falls out of
``itertools.product`` over no axes rather than needing a special case, which is
the small piece of evidence that the sweep machinery is not secretly assuming a
grid.

**The window is Phase 4's ``DEMO_WINDOW``**, and deliberately the same one:
that phase names it *"the configuration Phase 7 should seed from"*, and
``tests/test_reference_strategies.py`` measures the reference strategies over
it. Seeding from a different window would make the front page and the test two
unrelated measurements.

**The universe is the whole configured one, not a single symbol - which is a
deviation from the plan's wording, and it was measured rather than argued.**
Phase 5's bullet says *"on one synthetic symbol"*, and building it that way
produced a demo that says the opposite of what the plan intends. On ``ZVZZT``
alone over ``DEMO_WINDOW``, the baseline happens to fall 42% - a single
driftless five-year walk, doing what a coin flip with a standard deviation of
tens of percent does - and because the crossover is long only about half the
time, **all nineteen of them beat it**. A seeded front page showing nineteen
out of nineteen strategies beating buy-and-hold *on data with no alpha in it by
construction* is the exact false positive Phase 6 exists to stop being mistaken
for a finding, printed on the app's own home page before Phase 6 arrives.

Over the configured universe the same grid scatters around the baseline -
seven of nineteen ahead - because averaging five independent walks shrinks the
market's own realized move without touching the strategies. Nothing about the
strategies changed; the *variance of the thing they are being compared against*
did. That is Phase 4's corrected prediction restated: the head-to-head on any
one path is dominated by the market's own move, and the honest reading needs
either more paths or the selection accounting Phase 6 adds.

The sizing argument the plan gives for one symbol survives the change intact,
because the run count is what costs: nineteen runs plus a baseline either way,
each a fraction of a second, over five symbols instead of one.

**No claim is made here about who wins, and that is deliberate.** Phase 4
retired exactly that claim - *"a build gate asserting 'the crossover loses' on
one path asserts the sign of a coin flip"* - so this module asserts the
structural property instead, in ``tests/test_sweep.py``: the baseline covers the
identical window, universe, cash and cost model as the grid, which is what makes
the comparison on the page a comparison at all.

The symbols are Nasdaq's own reserved test tickers, per Phase 2's argument
about why a synthetic universe cannot be named SPY, so no screenshot of this
leaderboard can be mistaken for a claim about a real instrument.

**Sized against the cold-cluster requirement rather than guessed.** Twenty
combinations, one of which (``fast = slow = 20``) the strategy's own validator
prunes, over about 1,250 daily bars: nineteen runs plus the baseline, each a
fraction of a second of engine time, which finishes on one worker while
somebody is still looking at the page. Widening it is an edit here, which is
the point of it being here.
"""

from __future__ import annotations

from collections.abc import Sequence
from datetime import UTC, datetime
from decimal import Decimal
from typing import Final

from aerie_trading.providers.base import Interval

# The module rather than the package: `providers.synthetic` re-exports nothing
# on purpose, so that a process wanting a symbol list does not pay for pandas.
from aerie_trading.providers.synthetic.config import DEFAULT_UNIVERSE
from aerie_trading.runs.costs import RETAIL_EQUITY
from aerie_trading.runs.sweep import SweepSpec

__all__ = ["DEMO_SWEEPS", "DEMO_SYMBOLS", "DEMO_WINDOW", "demo_sweeps"]

#: Five years, matching Phase 4's ``DEMO_WINDOW`` exactly. See the module
#: docstring: the seeded leaderboard is a test's assertion made visible, and it
#: is only that if it covers the same window the test does.
DEMO_WINDOW: Final[tuple[datetime, datetime]] = (
    datetime(2021, 1, 1, tzinfo=UTC),
    datetime(2026, 1, 1, tzinfo=UTC),
)

#: The generator's default universe. Read from ``DEFAULT_UNIVERSE`` rather than
#: written out, so the demo covers what a fresh installation actually collects;
#: ``demo_sweeps`` takes an override for the installation that has re-configured
#: its universe, and the CLI passes what that installation collects bars for.
DEMO_SYMBOLS: Final[tuple[str, ...]] = tuple(entry.symbol for entry in DEFAULT_UNIVERSE)

#: Four fast windows and five slow ones. The step is coarse for the reason
#: ``MaCrossoverParams`` gives about its own declared step: a 50-bar and a
#: 51-bar average are the same idea, and a grid that walks both spends a sweep
#: re-measuring one hypothesis.
_FAST: Final[tuple[Decimal, ...]] = (Decimal(5), Decimal(10), Decimal(15), Decimal(20))
_SLOW: Final[tuple[Decimal, ...]] = (
    Decimal(20),
    Decimal(40),
    Decimal(60),
    Decimal(80),
    Decimal(100),
)


DEMO_SWEEPS: Final[tuple[SweepSpec, ...]] = (
    SweepSpec(
        name="demo-ma-crossover",
        strategy="ma_crossover",
        grid={"fast": _FAST, "slow": _SLOW},
        symbols=DEMO_SYMBOLS,
        interval=Interval.ONE_DAY,
        window_start=DEMO_WINDOW[0],
        window_end=DEMO_WINDOW[1],
        starting_cash=Decimal(100_000),
        costs=RETAIL_EQUITY,
        # Ahead of the default 100, so a demo seeded at first boot is not stuck
        # behind whatever an operator launched a moment earlier. It is twenty
        # runs; nothing is meaningfully delayed by letting them through first.
        priority=10,
    ),
    SweepSpec(
        name="demo-buy-and-hold",
        strategy="buy_and_hold",
        grid={},
        symbols=DEMO_SYMBOLS,
        interval=Interval.ONE_DAY,
        window_start=DEMO_WINDOW[0],
        window_end=DEMO_WINDOW[1],
        starting_cash=Decimal(100_000),
        costs=RETAIL_EQUITY,
        # Ahead of the grid it is the baseline for, so the comparison exists
        # from the moment the first crossover result lands rather than after
        # the last one.
        priority=5,
    ),
)


def demo_sweeps(symbols: Sequence[str] | None = None) -> tuple[SweepSpec, ...]:
    """The demo, as specs, optionally over a universe this installation collects.

    ``None`` means the generator's default universe, which is what a fresh
    clone has. An installation that replaced its universe wholesale
    (``TRADING_SYNTHETIC``) has a lake full of symbols these constants do not
    name, and a seeded demo whose every run failed with "collect before
    backtesting" would be a worse first impression than no demo - so the CLI
    passes what this installation actually collects bars for.

    The two sweeps are re-pointed together or not at all. A baseline over a
    different universe from the grid it sits beside is not a baseline.
    """
    if symbols is None:
        return DEMO_SWEEPS
    return tuple(spec.model_copy(update={"symbols": tuple(symbols)}) for spec in DEMO_SWEEPS)
