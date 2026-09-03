"""The synthetic market: deterministic, stateless, and free of alpha.

docs/plans/trading.md Phase 2. What it is for, in one paragraph, because it is
easy to mistake for a test fixture: it is the source every phase between here
and Schwab is built and hardened against, so that Phase 8 is a provider class
dropped into a collector that already works rather than a scramble to write the
collector while a wall clock runs. It is also the plan's null hypothesis -
zero alpha by construction, so a strategy that posts an attractive Sharpe
against it is provably overfit, which is what turns Phase 6's gate from a
judgment call into arithmetic.

It is pure noise, permanently. No implied-volatility surface, no jumps, no
regime switching, no microstructure. The moment it becomes interesting it stops
being a control and starts being a thing whose own behaviour has to be reasoned
about before any result measured against it means anything.

**This package deliberately re-exports nothing.** Importing it has to stay
free, because ``Settings`` carries a ``SyntheticConfig`` and therefore every
process in the silo imports ``.config`` - including the migration init
container, which runs under a 256Mi limit
(``deploy/cluster/trading/app/deployment.yaml``). A convenience re-export here
would put ``exchange_calendars`` and its pandas dependency, half a second and
something like a hundred megabytes, into a process whose entire job is
``alembic upgrade head``. Import the module you want::

    from aerie_trading.providers.synthetic.config import SyntheticConfig
    from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
"""
