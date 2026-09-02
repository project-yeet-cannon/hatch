"""The control plane: one FastAPI process, and everything the platform asks of it.

At Phase 1 that is deliberately all it is - liveness, readiness, metrics and a
version endpoint, with no trading logic to confuse them with. The phase is
judged on whether the deploy pipeline, the logs, the metrics and the ingress
all work before there is anything interesting behind them, which is only
checkable while there is nothing interesting behind them.
"""

from aerie_trading.control.app import create_app

__all__ = ["create_app"]
