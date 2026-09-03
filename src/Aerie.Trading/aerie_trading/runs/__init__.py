"""Runs, sweeps and the queue that carries them - docs/plans/trading.md Phase 5.

The package is deliberately import-light at its top level, matching
``providers/synthetic/``: it re-exports nothing. The worker imports the lake
(duckdb and polars, 90 MB), the launcher imports the market data provider
(pandas, another 142 MB), and neither should pay for the other because they
happen to be spelled under one package name. The migration init container, which
runs under a 256 Mi limit, imports neither.

The pieces, in the order a run passes through them:

``sweep.py``
    a grid, expanded, estimated and - only after the estimate is confirmed -
    written to the queue.
``catalog.py``
    the ``strategy``, ``param_set`` and ``instrument`` rows a run points at.
``queue.py``
    ``SELECT ... FOR UPDATE SKIP LOCKED``, the lease, and the fence that makes
    a duplicated execution unable to become a duplicated row.
``worker.py``
    claim, backtest, record, repeat.
``metrics``
    is *not* here - it is ``engine/metrics.py``, because a metric is a function
    of a ``BacktestResult`` and nothing else, and putting it here would let the
    control plane compute one a second way.
``demo.py``
    the named demo sweep, sized once in code.
"""
