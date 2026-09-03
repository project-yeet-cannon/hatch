"""What a fresh installation finds when it opens the control panel.

docs/plans/trading.md Phase 7: *"A control panel deployed against an empty
database is a shipped framework: every screen renders an empty state and a
button. What was asked for is to deploy this on autopilot, open it in
production, and find a working product to react to."*

So this package puts something in it, in three steps, each idempotent and each
a precondition of the next:

1. **The strategies**, from ``strategies.REGISTRY``. A ``strategy`` row per
   entry, refreshed rather than skipped - ``runs/catalog.ensure_strategy``
   holds the argument for that.
2. **The history**, because the demo runs read the Lake and a cold cluster's
   Lake is empty. The daily collector's CronJob fires after the next close and
   collects *one session*; a leaderboard whose every run failed with "collect
   before backtesting" is a worse first impression than no leaderboard, so the
   seed backfills the demo's window itself if the bars are not already there.
3. **The demo sweeps** (``runs/demo.py``), enqueued and marked ``seeded``.

**It computes rather than inserts, and the plan says why at length:**
*"Committed result rows would be a decoration that proves nothing and rots the
first time a metric definition changes; computed ones prove the whole pipeline
- queue, worker, engine, metrics, serializer - works in production."* Nothing
here writes a ``run_metric``. It writes queue rows and lets the workers do
what they do, which is why a cold boot shows runs in flight for a minute
before it shows results.

**Idempotent, and namespaced to itself.** Every step asks what is already
there. The sweeps are keyed on ``(name, seeded)``: a batch this job wrote is
left exactly alone on the next deploy, and a sweep an *operator* launched is
invisible to it even if they happened to give it the same name - which they
can, because ``sweep.name`` is deliberately not unique. That is the plan's
*"it must never touch a strategy, sweep or run the owner added"*, made a
property of a column rather than of a naming convention.

**Re-running is every deploy**, so "changes nothing" has to mean nothing: no
new rows, no re-enqueued grid, no re-collected month of bars. The report this
returns says which of the three steps did anything, and on a second run the
answer is none of them.
"""

from aerie_trading.seed.seeding import (
    SeedReport,
    ensure_history,
    ensure_strategies,
    ensure_sweeps,
    seed,
)

__all__ = [
    "SeedReport",
    "ensure_history",
    "ensure_strategies",
    "ensure_sweeps",
    "seed",
]
