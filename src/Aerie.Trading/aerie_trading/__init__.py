"""Aerie's trading silo.

Empty by design at Phase 0b (docs/plans/trading.md): this phase ships a
toolchain and a CI lane so that the phases with a wall-clock cost - a Schwab
credential and the option-chain collector behind it - have somewhere to land
the day their approvals do. The subpackages the plan names (``control``,
``providers``, ``engine``, ``collect``, ``strategies``) arrive with the phases
that need them.

Nothing here may import from ``src/Aerie.Api/``, and no ``.csproj`` may
reference this directory. That is the extraction seam, and Phase 1 turns it
into a build failure rather than a convention.
"""

# Read from the installed distribution rather than repeated as a literal, so
# pyproject.toml stays the one place a version is written down.
from importlib.metadata import version

__all__ = ["__version__"]

__version__: str = version("aerie-trading")
