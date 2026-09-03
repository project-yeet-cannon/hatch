"""The control panel: the four screens' data, and the sweep launcher.

docs/plans/trading.md Phase 7. Three modules, split by what they are
responsible for rather than by what they are about:

``views.py``
    what a response *is*. Every model here is a projection of rows Phases 1-6
    already write, and the honesty layer's ``Figures`` is carried whole rather
    than flattened into columns.
``reader.py``
    the queries. One row shape, built in one place, used by every screen.
``launch.py``
    the only thing in this package that writes, and the estimate-and-confirm
    handshake it writes behind.
``routes.py``
    the HTTP surface, and one place where an exception becomes a status.

The SPA that consumes all of it lives outside this repository directory, in
``src/Aerie.Web/apps/trading/`` - the workspace where every Aerie app is built
from the same design system. It is served by *this* service rather than by
Aerie.Api (``control/spa.py``), which is the seam Phase 7 names: the trading
silo ships its own interface, so the day it becomes its own repository the app
goes with it.
"""

from aerie_trading.control.panel.launch import Launcher, LaunchRequest
from aerie_trading.control.panel.reader import Panel
from aerie_trading.control.panel.routes import create_panel_router

__all__ = ["LaunchRequest", "Launcher", "Panel", "create_panel_router"]
