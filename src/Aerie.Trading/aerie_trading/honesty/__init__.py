"""The honesty layer - docs/plans/trading.md Phase 6.

*"Ships: results that can be trusted, or at least whose untrustworthiness is
visible. This phase adds no capability and is the most valuable one in the
plan."*

By Phase 5 the silo can produce ten thousand results, some of which will look
excellent for no reason at all. Nothing in this package makes a strategy
better; every module exists to keep a number from being mistaken for a finding.

===================  ======================================================
``config``           the knobs, on ``Settings.honesty``.
``windows``          rolling train/test folds over a run's window.
``selection``        what "best of N" is worth, arithmetically.
``walkforward``      parameters chosen on train, equity carried through test.
``scoring``          the metrics a worker writes beside every run.
``presentation``     the guard that refuses an in-sample headline.
===================  ======================================================

**Separate from ``engine/``, deliberately.** The engine answers "what did this
strategy do over this data"; everything here answers "and what is that worth",
which is a question about *several* runs - a baseline, a stressed re-score, the
siblings a sweep produced - and about the window a result may honestly be
quoted over. ``engine/metrics.py`` says the same thing from its own side: it
holds only what one ``BacktestResult`` can answer alone.
"""
