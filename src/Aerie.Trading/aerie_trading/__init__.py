"""Aerie's trading silo.

At Phase 1 (docs/plans/trading.md) this is a control plane with nothing to
control: ``control`` serves health, readiness, metrics and a version endpoint,
``db`` holds the Ledger's first three tables, and ``migrations`` builds them.
Deliberately - the phase is judged on whether the deploy pipeline, the logs,
the metrics and the ingress all work, which is only checkable while there is
nothing interesting behind them. The subpackages the plan names next
(``providers``, ``collect``, ``engine``, ``strategies``) arrive with the phases
that need them, and the two with a wall-clock cost - a Schwab credential and
the option-chain collector behind it - now have somewhere to land the day their
approvals do.

Nothing here may import from the other side of the repository, and no
``.csproj`` may reference this directory. That is the extraction seam, and the
``trading-boundary`` job in .github/workflows/ci.yml makes it a build failure
rather than a convention.
"""

# Read from the installed distribution rather than repeated as a literal, so
# pyproject.toml stays the one place a version is written down.
from importlib.metadata import version

__all__ = ["__version__"]

__version__: str = version("aerie-trading")
