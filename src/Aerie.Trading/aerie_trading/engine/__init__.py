"""The engine: one loop, two clocks, and a portfolio that is already multi-leg.

docs/plans/trading.md Phase 4. The phase ships a backtest, but almost nothing
in this package is about backtesting specifically - that is the whole design.
*One engine, two clocks* is the plan's own heading, and the property it asks
for is that a strategy written today against ``ReplayClock`` runs unchanged
against Phase 9's ``LiveClock``. So the modules split along the seams that
separate "what a strategy sees" from "where the data came from":

======================  ===================================================
``engine.money``        decimal arithmetic, and where prices stop being floats.
``engine.clock``        the ``Clock`` protocol and ``ReplayClock``.
``engine.instruments``  an equity or an option contract, behind one type.
``engine.portfolio``    legs, cash, marks, and realized and unrealized P&L.
``engine.broker``       the ``Broker`` protocol, ``SimBroker``, and its costs.
``engine.strategy``     the ``Strategy`` protocol, its params, and the context.
``engine.history``      bars, aligned on one timeline, with a cursor on it.
``engine.backtest``     the loop that drives all of the above.
======================  ===================================================

**This package re-exports nothing**, for the reason ``lake/__init__.py`` gives
at length: the migration init container runs under a 256 Mi limit and must not
pay for polars because something it imports re-exported something that needed
it. Only ``engine.history`` reaches the lake; everything else here is stdlib
and pydantic, and the fixture test that proves the accounting to the cent
constructs its bars by hand without touching either.
"""
