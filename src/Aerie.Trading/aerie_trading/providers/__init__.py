"""Market data, and the one interface every source of it implements.

``base`` is the interface (docs/plans/trading.md Phase 2); ``synthetic`` is its
first implementation and ``schwab`` is Phase 8's second. The plan is explicit
about the order and about why: a protocol whose only implementation is a vendor
API ends up encoding that vendor's quirks as though they were the shape of
market data, so this one has to satisfy a trivial implementation first and a
real one second.

``calendar`` is shared by both. A session's boundaries are a property of the
exchange rather than of whoever is reporting prices, and a provider that
answered ``market_hours()`` out of its own head would be a second opinion about
a fact - which is exactly the kind of disagreement that is discovered by a
backtest that silently crossed a holiday.
"""
