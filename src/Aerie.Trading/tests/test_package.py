"""The smoke test that makes an empty package's lane mean something.

There is no behavior to test at Phase 0b, but there is a claim: that
`uv sync --frozen` builds an environment in which this project is installed and
importable. A lane with no tests at all would report success for a broken
install, and pytest exits non-zero on an empty collection anyway - so the
minimum honest test is the one that exercises the install itself.
"""

import aerie_trading


def test_package_is_installed_and_reports_its_version() -> None:
    # Resolved from the installed distribution's metadata, so this fails if the
    # package was merely on sys.path rather than installed by `uv sync`.
    assert aerie_trading.__version__
