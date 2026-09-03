"""The loop. Four steps per bar, in an order that is the whole argument.

docs/plans/trading.md Phase 4 ships "a backtest of two boring strategies over
collected equity data, producing a result a person can check by hand". This is
the driver, and almost all of its content is the ordering below::

    for each instant on the timeline:
        1. fill the orders queued on the previous bar, at this bar's OPEN
        2. mark every position to this bar's CLOSE
        3. run the strategy, which sees bars up to and including this one
        4. record a point on the equity curve

Step 1 before step 3 is the no-same-bar-fill rule made structural: by the time
a strategy runs, the queue it can add to is a queue nothing will read until the
*next* iteration. Step 2 before step 3 means a strategy reads a portfolio
already marked to the price it is looking at, rather than to yesterday's.
Step 4 last means the curve's point for a bar includes everything that bar did.

**A run is a value, not a side effect.** ``run_backtest`` returns a
``BacktestResult`` holding the curve, the fills and the parameters, and writes
nothing anywhere - the Ledger's ``run`` and ``trade`` tables are Phase 5, and
an engine that wrote to them would be an engine that cannot be exercised
without a database. The result knows how to hash itself, which is what Phase
4's determinism gate compares and what Phase 5's ``run`` row will store.

**Terminal positions are marked, not liquidated.** A backtest that force-sold
everything on the last bar would charge a strategy for an exit it did not
choose, which flatters whatever was already flat and penalises whatever was
holding - and ``buy_and_hold``, the baseline every other result is measured
against, is the strategy that is always holding. Final equity is therefore
mark-to-market at the last close.
"""

from __future__ import annotations

import hashlib
import json
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Any

from aerie_trading.engine.broker import Costs, ExpiredOrder, Fill, SimBroker
from aerie_trading.engine.clock import ReplayClock
from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.money import ZERO, money, to_cents
from aerie_trading.engine.portfolio import Portfolio
from aerie_trading.engine.strategy import Strategy, StrategyContext

__all__ = ["END_OF_DATA", "BacktestResult", "EquityPoint", "compare", "run_backtest"]

#: The reason recorded against orders still queued when the data runs out. A
#: constant because a test asserts on it: an order expiring on the last bar is
#: ordinary, and one expiring earlier means a symbol stopped printing bars
#: mid-run, which is a data gap rather than a strategy decision.
END_OF_DATA = "the run ended before a bar was available to fill against"


@dataclass(frozen=True, slots=True)
class EquityPoint:
    """One row of the equity curve, at the close of one bar."""

    timestamp: datetime
    cash: Decimal
    market_value: Decimal
    realized_pnl: Decimal
    unrealized_pnl: Decimal

    @property
    def equity(self) -> Decimal:
        return self.cash + self.market_value


@dataclass(frozen=True, slots=True)
class BacktestResult:
    """Everything one run produced, detached from the objects that produced it.

    Frozen and self-contained so that a sweep can hold ten thousand of them
    without also holding ten thousand portfolios, and so that ``fingerprint``
    is a function of the result rather than of the engine state that happened
    to still be alive.
    """

    strategy: str
    params: Mapping[str, object]
    symbols: tuple[str, ...]
    interval: str
    started_at: datetime
    ended_at: datetime
    bars: int
    starting_cash: Decimal
    curve: tuple[EquityPoint, ...]
    fills: tuple[Fill, ...]
    expired: tuple[ExpiredOrder, ...]
    costs: Mapping[str, str]

    # -- the numbers a leaderboard sorts on ---------------------------------

    @property
    def final_equity(self) -> Decimal:
        """Cash plus positions at the last close. Not liquidated - see the module docstring."""
        return self.curve[-1].equity if self.curve else self.starting_cash

    @property
    def total_return(self) -> Decimal:
        """Final equity over starting cash, less one. The headline, and only that.

        Deliberately the only return metric here. Sharpe, drawdown-adjusted
        returns and the rest belong beside the in-sample/out-of-sample windows
        that make them mean anything, which is Phase 6 - a Sharpe computed in
        Phase 4 would be a number the UI could show before the honesty layer
        exists to qualify it.
        """
        if self.starting_cash == 0:
            raise ZeroDivisionError("a run that started with no cash has no return")
        return self.final_equity / self.starting_cash - 1

    @property
    def max_drawdown(self) -> Decimal:
        """The deepest peak-to-trough fall in equity, as a non-negative fraction.

        Here rather than in Phase 6 because it is a property of this run alone
        and needs no baseline to interpret: it is the worst the account looked
        during the run, which is the one risk number an operator asks for
        before asking anything else.
        """
        peak = self.starting_cash
        worst = ZERO
        for point in self.curve:
            peak = max(peak, point.equity)
            if peak > 0:
                worst = max(worst, (peak - point.equity) / peak)
        return worst

    @property
    def commissions(self) -> Decimal:
        return sum((fill.commission for fill in self.fills), start=ZERO)

    @property
    def slippage_cost(self) -> Decimal:
        """Cash given up to slippage across every fill. The other half of the cost story."""
        return sum((fill.slippage_cost for fill in self.fills), start=ZERO)

    @property
    def trades(self) -> int:
        return len(self.fills)

    # -- identity -----------------------------------------------------------

    def canonical(self) -> bytes:
        """The run as bytes, for comparing two of them.

        Every number is rendered as a decimal string rather than as a JSON
        number, because a JSON float would put the engine's exact decimal
        accounting through a binary round trip on the way to being compared -
        which is the one place a determinism check must not lose precision.
        Sorted keys and no whitespace, so the bytes depend on the values and
        not on dictionary insertion order.
        """
        payload: dict[str, Any] = {
            "strategy": self.strategy,
            "params": {key: str(value) for key, value in sorted(self.params.items())},
            "symbols": list(self.symbols),
            "interval": self.interval,
            "started_at": self.started_at.isoformat(),
            "ended_at": self.ended_at.isoformat(),
            "bars": self.bars,
            "starting_cash": str(self.starting_cash),
            "costs": dict(sorted(self.costs.items())),
            "curve": [
                [
                    point.timestamp.isoformat(),
                    str(point.cash),
                    str(point.market_value),
                    str(point.realized_pnl),
                    str(point.unrealized_pnl),
                ]
                for point in self.curve
            ],
            "fills": [
                [
                    fill.order.id,
                    fill.filled_at.isoformat(),
                    fill.instrument.symbol,
                    fill.quantity,
                    str(fill.price),
                    str(fill.commission),
                    str(fill.realized_pnl),
                    fill.position_key,
                    fill.order.tag,
                ]
                for fill in self.fills
            ],
            "expired": [
                [order.order.id, order.order.instrument.symbol, order.reason]
                for order in self.expired
            ],
        }
        return json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")

    def fingerprint(self) -> str:
        """A sha256 over ``canonical()``.

        What Phase 4's determinism gate compares and what Phase 5's ``run`` row
        stores: a short value that two runs either agree on or do not, with no
        judgement about how close is close enough. A backtest that cannot be
        reproduced cannot be debugged, and a comparison between two that were
        each produced once means nothing.
        """
        return hashlib.sha256(self.canonical()).hexdigest()


def run_backtest(
    strategy: Strategy,
    history: BarHistory,
    starting_cash: Decimal | float | int | str = 100_000,
    costs: Costs | None = None,
    name: str = "",
) -> BacktestResult:
    """Drive ``strategy`` over ``history`` and return what happened.

    ``name`` labels the result; it defaults to the strategy class's name, which
    is right for an ad-hoc run and wrong for a sweep, where the caller passes
    the registered ``StrategySpec.name`` so that ten thousand results agree on
    what produced them.
    """
    portfolio = Portfolio(starting_cash)
    broker = SimBroker(portfolio, costs)
    clock = ReplayClock(history.timeline)
    curve: list[EquityPoint] = []

    for index, now in enumerate(clock.ticks()):
        # 1. Orders queued on an earlier bar, against this bar's open. An order
        #    whose instrument has no bar here stays queued rather than being
        #    cancelled - see SimBroker.fill_at.
        broker.fill_at(now, history.opens_at(index))
        # 2. Mark to this bar's close, carrying the last known price forward
        #    for anything that did not print.
        portfolio.mark_all(history.marks_at(index))
        # 3. The strategy, which can reach nothing but this context.
        strategy.on_bar(StrategyContext(now, index, history, portfolio, broker))
        # 4. The curve point for this bar, after everything the bar did.
        snapshot = portfolio.snapshot()
        curve.append(
            EquityPoint(
                timestamp=now,
                cash=snapshot.cash,
                market_value=snapshot.market_value,
                realized_pnl=snapshot.realized_pnl,
                unrealized_pnl=snapshot.unrealized_pnl,
            )
        )

    broker.expire_all(END_OF_DATA)

    return BacktestResult(
        strategy=name or type(strategy).__name__,
        params=strategy.params.model_dump(),
        symbols=history.symbols,
        interval=history.interval.value,
        started_at=history.timeline[0],
        ended_at=history.timeline[-1],
        bars=len(history.timeline),
        starting_cash=to_cents(money(starting_cash)),
        curve=tuple(curve),
        fills=tuple(broker.fills),
        expired=tuple(broker.expired),
        costs=broker.costs.describe(),
    )


def compare(results: Sequence[BacktestResult]) -> tuple[BacktestResult, ...]:
    """``results`` ordered best-first by total return.

    The smallest possible leaderboard, and it is here so that Phase 5 and the
    tests that assert one strategy beats another sort the same way. Ties break
    on the fingerprint, which is arbitrary but stable - an ordering that
    depended on input order would make a sweep's leaderboard depend on the
    order its workers happened to finish in.
    """
    return tuple(sorted(results, key=lambda run: (-run.total_return, run.fingerprint())))
