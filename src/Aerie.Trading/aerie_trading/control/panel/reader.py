"""The queries behind the four screens.

docs/plans/trading.md Phase 7 asks for a strategies list, a strategy detail, a
leaderboard and a run detail. Every one of them is a projection of rows Phases
1 through 6 already write, and this module is the whole of that projection.

**One row shape, built in one place.** ``_rows`` turns a list of run ids into
``RunRow``s, and the leaderboard, a strategy's best result, a sweep's contents
and the run detail all go through it. The alternative - a query per screen,
each selecting the columns that screen happens to need - is how the honesty
columns end up missing from the one table somebody actually reads, and it is
how two screens come to disagree about the same run.

**Two queries per screen, never one per row.** The row builder takes ids, then
fetches metrics for all of them in a second statement. A leaderboard that
looked up its own metrics one row at a time would be fifty round trips behind
a control plane whose whole job is to answer quickly while a sweep is running.

**Text SQL, matching ``runs/queue.py``.** These are read-only statements over a
schema this package owns, and the ORM's contribution to them would be to
express the same joins less legibly. Parameters are always bound, never
interpolated - including the sort metric, which is a value in a ``WHERE``
rather than a column name.

**The sort metric cannot inject anything, by construction.** It arrives as a
name and is bound as a *value* into ``run_metric.name = :sort``; the ORDER BY
is over that join's ``value`` column, which is fixed. So an unknown metric name
produces an empty board rather than an error - and, importantly, rather than a
column name somebody typed into a query.
"""

from __future__ import annotations

import logging
from collections.abc import Iterable, Mapping, Sequence
from datetime import datetime
from decimal import Decimal
from typing import Final

from pydantic_core import PydanticUndefined
from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.control.panel.views import (
    CurveView,
    FoldView,
    LeaderboardView,
    ParameterView,
    RunCounts,
    RunDetail,
    RunRow,
    SourceView,
    StrategyCard,
    StrategyDetail,
    SweepProgressView,
    SweepRow,
    TradeView,
)
from aerie_trading.db.models import RunStatus
from aerie_trading.engine.metrics import SHARPE
from aerie_trading.engine.strategy import Params
from aerie_trading.honesty.presentation import figures_for
from aerie_trading.strategies import REGISTRY

__all__ = [
    "DEFAULT_LIMIT",
    "MAX_LIMIT",
    "Panel",
    "UnknownRun",
    "UnknownStrategy",
    "UnknownSweep",
]

logger = logging.getLogger(__name__)

#: How many rows a board returns when nobody said. Fifty is a screenful and a
#: half; the ceiling below is what stops a client asking for a table nobody can
#: read and this pod cannot serialize quickly.
DEFAULT_LIMIT: Final = 50
MAX_LIMIT: Final = 500

#: The metric a board sorts on when nobody names one. Risk-adjusted rather than
#: total return, because a leaderboard sorted on return alone rewards whichever
#: run took the most leverage-equivalent risk, which is the first thing this
#: whole phase is meant not to do.
DEFAULT_SORT: Final = SHARPE

#: The three windows the plan names: *"sliceable by today / this week / since
#: inception"*. Rendered as SQL against ``finished_at``, in UTC - the nodes run
#: UTC and every timestamp in this schema is stored in it, so "today" is the
#: same day for the database, the pod and the metrics beside them.
_SINCE: Final[Mapping[str, str]] = {
    "inception": "",
    "today": " AND r.finished_at >= date_trunc('day', now())",
    "week": " AND r.finished_at >= now() - interval '7 days'",
}

#: How a board slices by whether a figure was fitted. ``out_of_sample`` is the
#: default because it is the only slice whose numbers may be called
#: performance - see ``honesty/presentation.py``, which refuses to serve the
#: others under that label whatever this filter does.
_SAMPLE: Final[Mapping[str, str]] = {
    "out_of_sample": " AND r.oos_start IS NOT NULL",
    "in_sample": " AND r.oos_start IS NULL",
    "all": "",
}

#: Backtest or live. There is no live clock in this build, so ``live`` matches
#: nothing - deliberately a filter that returns an empty board rather than an
#: option the UI does not offer, because "nothing has traded live" is a true
#: and useful answer and an absent control is not.
_MODE: Final[Mapping[str, str]] = {
    "backtest": "",
    "live": " AND false",
}


class UnknownStrategy(LookupError):
    """No ``strategy`` row by that name."""


class UnknownRun(LookupError):
    """No ``run`` row by that id."""


class UnknownSweep(LookupError):
    """No ``sweep`` row by that id."""


class Panel:
    """Every read the control panel makes, over one engine.

    Holds no state between calls and opens a session per method, matching
    ``RunQueue``: this is a control plane answering concurrent requests, and a
    session that outlived a request would be a transaction held open across
    whatever the client did next.
    """

    def __init__(self, engine: Engine) -> None:
        self._engine = engine

    # -- strategies ---------------------------------------------------------

    def strategies(self) -> tuple[StrategyCard, ...]:
        """Every strategy the Ledger knows, shipped or not.

        Driven by the ``strategy`` table rather than by ``REGISTRY``, and the
        difference is the point: a build that dropped a strategy still has
        results from it on the leaderboard, and a list that read only the
        registry would show a board full of runs of something it claims does
        not exist. ``shipped`` is where the two are reconciled.
        """
        with Session(self._engine) as session:
            rows = session.execute(
                text("SELECT id, name, description FROM strategy ORDER BY name")
            ).all()
            if not rows:
                return ()
            counts = self._run_counts(session)
            sweeps = self._sweep_counts(session)
            last = self._last_finished(session)
            best = self._best_runs(session, [int(row.id) for row in rows])
            return tuple(
                self._card(
                    strategy_id=int(row.id),
                    name=str(row.name),
                    description=str(row.description),
                    counts=counts,
                    sweeps=sweeps,
                    last=last,
                    best=best,
                )
                for row in rows
            )

    def strategy(self, name: str, limit: int = DEFAULT_LIMIT) -> StrategyDetail:
        """One strategy, its batches, and its most recent runs."""
        with Session(self._engine) as session:
            row = session.execute(
                text("SELECT id, name, description FROM strategy WHERE name = :name"),
                {"name": name},
            ).one_or_none()
            if row is None:
                raise UnknownStrategy(f"no strategy named {name!r}")

            strategy_id = int(row.id)
            card = self._card(
                strategy_id=strategy_id,
                name=str(row.name),
                description=str(row.description),
                counts=self._run_counts(session, strategy_id),
                sweeps=self._sweep_counts(session, strategy_id),
                last=self._last_finished(session, strategy_id),
                best=self._best_runs(session, [strategy_id]),
            )
            sweeps = self._sweeps(session, strategy_id=strategy_id)
            # Newest first, and every status: a strategy detail is where
            # somebody goes when a sweep they just launched is not producing
            # results, so a list that showed only successes would hide the
            # answer.
            ids = [
                int(entry.id)
                for entry in session.execute(
                    text(
                        """
                        SELECT id FROM run
                         WHERE strategy_id = :strategy_id
                         ORDER BY COALESCE(finished_at, started_at, enqueued_at) DESC, id DESC
                         LIMIT :limit
                        """
                    ),
                    {"strategy_id": strategy_id, "limit": _bounded(limit)},
                ).all()
            ]
            return StrategyDetail(strategy=card, sweeps=sweeps, runs=self._rows(session, ids))

    # -- the leaderboard ----------------------------------------------------

    def leaderboard(
        self,
        sort: str = DEFAULT_SORT,
        sample: str = "out_of_sample",
        since: str = "inception",
        mode: str = "backtest",
        strategy: str | None = None,
        source: str | None = None,
        limit: int = DEFAULT_LIMIT,
    ) -> LeaderboardView:
        """*"What did best today"* - the ask's own success criterion.

        Every filter is validated against a fixed mapping before it becomes
        SQL. An unknown one raises rather than falling back to a default: a
        board that silently answered a different question from the one asked is
        the failure mode a leaderboard cannot afford.
        """
        for label, value, allowed in (
            ("sample", sample, _SAMPLE),
            ("since", since, _SINCE),
            ("mode", mode, _MODE),
        ):
            if value not in allowed:
                raise ValueError(f"{label} must be one of {sorted(allowed)}, not {value!r}")

        clauses = _SAMPLE[sample] + _SINCE[since] + _MODE[mode]
        if strategy is not None:
            clauses += " AND st.name = :strategy"
        if source is not None:
            clauses += " AND ds.name = :source"

        with Session(self._engine) as session:
            ids = [
                int(row.id)
                for row in session.execute(
                    text(
                        f"""
                        SELECT r.id
                          FROM run r
                          JOIN strategy st ON st.id = r.strategy_id
                          JOIN data_source ds ON ds.id = r.data_source_id
                          JOIN run_metric m ON m.run_id = r.id AND m.name = :sort
                         WHERE r.status = 'succeeded'{clauses}
                         ORDER BY m.value DESC, r.id
                         LIMIT :limit
                        """
                        # `clauses` is assembled from the fixed mappings above and
                        # holds no caller-supplied text; every value, the sort
                        # metric included, is bound below.
                    ),
                    {
                        "sort": sort,
                        "strategy": strategy,
                        "source": source,
                        "limit": _bounded(limit),
                    },
                ).all()
            ]
            return LeaderboardView(
                rows=self._rows(session, ids),
                sort=sort,
                sample=sample,
                since=since,
                mode=mode,
                note=(
                    "Nothing has traded live: every result here was produced by the engine"
                    " against collected history."
                    if mode == "live"
                    else None
                ),
            )

    # -- one run ------------------------------------------------------------

    def run(self, run_id: int) -> RunDetail:
        """*"Trades, the equity curve, the metrics, and the exact parameters."*"""
        with Session(self._engine) as session:
            rows = self._rows(session, [run_id])
            if not rows:
                raise UnknownRun(f"no run {run_id}")
            row = rows[0]
            sweeps = (
                self._sweeps(session, sweep_id=row.sweep_id) if row.sweep_id is not None else ()
            )
            return RunDetail(
                run=row,
                sweep=sweeps[0] if sweeps else None,
                trades=self._trades(session, run_id),
                curve=self._curve(session, run_id),
                folds=self._folds(session, run_id),
            )

    def sweep(self, sweep_id: int, limit: int = MAX_LIMIT) -> StrategyDetail:
        """One batch and its runs, shaped as a strategy detail of one sweep.

        The same type because it is the same screen: a sweep is a strategy's
        results narrowed to one launch, and a second model would be a second
        place the honesty columns have to be remembered.
        """
        with Session(self._engine) as session:
            sweeps = self._sweeps(session, sweep_id=sweep_id)
            if not sweeps:
                raise UnknownSweep(f"no sweep {sweep_id}")
            batch = sweeps[0]
            ids = [
                int(entry.id)
                for entry in session.execute(
                    text(
                        """
                        SELECT id FROM run
                         WHERE sweep_id = :sweep_id
                         ORDER BY kind DESC, id
                         LIMIT :limit
                        """
                    ),
                    {"sweep_id": sweep_id, "limit": _bounded(limit)},
                ).all()
            ]
            card = self.strategy(batch.strategy).strategy
            return StrategyDetail(strategy=card, sweeps=sweeps, runs=self._rows(session, ids))

    def queue(self) -> RunCounts:
        """Runs by status across the whole Ledger. The bar above every screen."""
        with Session(self._engine) as session:
            return _total(self._run_counts(session).values())

    # -- the pieces ---------------------------------------------------------

    def _card(
        self,
        strategy_id: int,
        name: str,
        description: str,
        counts: Mapping[int, RunCounts],
        sweeps: Mapping[int, int],
        last: Mapping[int, datetime],
        best: Mapping[int, RunRow],
    ) -> StrategyCard:
        spec = REGISTRY.get(name)
        return StrategyCard(
            name=name,
            description=description,
            shipped=spec is not None,
            parameters=() if spec is None else parameters_of(spec.params_model),
            sweeps=sweeps.get(strategy_id, 0),
            runs=counts.get(strategy_id, RunCounts()),
            last_finished_at=last.get(strategy_id),
            best=best.get(strategy_id),
        )

    def _rows(self, session: Session, run_ids: Sequence[int]) -> tuple[RunRow, ...]:
        """``RunRow`` per id, in the order given.

        The order is the caller's because it is the answer: a leaderboard's
        ordering is what a leaderboard *is*, and re-sorting here on anything -
        even on the same column - would be a second opinion about it.
        """
        if not run_ids:
            return ()
        rows = session.execute(
            text(
                """
                SELECT r.id, r.kind, r.status, r.symbols, r.interval,
                       r.window_start, r.window_end, r.oos_start, r.oos_end,
                       r.starting_cash, r.costs, r.attempts,
                       r.enqueued_at, r.started_at, r.finished_at, r.duration_ms, r.bars,
                       r.aerie_revision, r.data_fingerprint, r.result_fingerprint, r.error,
                       st.name AS strategy,
                       ps.id AS param_set_id, ps.params AS params,
                       sw.id AS sweep_id, sw.name AS sweep_name,
                       sw.trials AS trials, sw.seeded AS seeded,
                       ds.id AS source_id, ds.name AS source_name,
                       ds.description AS source_description
                  FROM run r
                  JOIN strategy st ON st.id = r.strategy_id
                  JOIN data_source ds ON ds.id = r.data_source_id
                  LEFT JOIN param_set ps ON ps.id = r.param_set_id
                  LEFT JOIN sweep sw ON sw.id = r.sweep_id
                 WHERE r.id = ANY(:ids)
                """
            ),
            {"ids": list(run_ids)},
        ).all()

        metrics = self._metrics(session, run_ids)
        built = {
            int(row.id): RunRow(
                id=int(row.id),
                kind=str(row.kind),
                status=str(row.status),
                strategy=str(row.strategy),
                params=row.params,
                param_set_id=row.param_set_id,
                sweep_id=row.sweep_id,
                sweep_name=row.sweep_name,
                trials=row.trials,
                seeded=bool(row.seeded) if row.seeded is not None else False,
                source=SourceView(
                    id=int(row.source_id),
                    name=str(row.source_name),
                    description=row.source_description,
                ),
                symbols=tuple(row.symbols),
                interval=str(row.interval),
                window_start=row.window_start,
                window_end=row.window_end,
                starting_cash=row.starting_cash,
                costs=row.costs,
                enqueued_at=row.enqueued_at,
                started_at=row.started_at,
                finished_at=row.finished_at,
                duration_ms=row.duration_ms,
                bars=row.bars,
                attempts=int(row.attempts),
                aerie_revision=row.aerie_revision,
                data_fingerprint=row.data_fingerprint,
                result_fingerprint=row.result_fingerprint,
                error=row.error,
                # The refusal, at the one point every number leaves this
                # process. A run with no out-of-sample window gets its figures
                # under `in_sample` and nothing under `headline`, and no route
                # here is in a position to decide otherwise.
                figures=figures_for(metrics.get(int(row.id), {}), row.oos_start, row.oos_end),
            )
            for row in rows
        }
        return tuple(built[run_id] for run_id in run_ids if run_id in built)

    def _metrics(
        self, session: Session, run_ids: Sequence[int]
    ) -> Mapping[int, Mapping[str, Decimal]]:
        out: dict[int, dict[str, Decimal]] = {}
        for row in session.execute(
            text("SELECT run_id, name, value FROM run_metric WHERE run_id = ANY(:ids)"),
            {"ids": list(run_ids)},
        ).all():
            out.setdefault(int(row.run_id), {})[str(row.name)] = row.value
        return out

    def _run_counts(self, session: Session, strategy_id: int | None = None) -> dict[int, RunCounts]:
        """Runs by status, keyed by strategy - or by 0 for the whole queue.

        One query for every strategy rather than one per card: a list screen
        that issued a count query per strategy would get slower with every
        strategy this installation ever registered.
        """
        clause = "" if strategy_id is None else " WHERE strategy_id = :strategy_id"
        rows = session.execute(
            text(
                f"SELECT strategy_id, status, count(*) AS runs FROM run{clause}"
                " GROUP BY strategy_id, status"
            ),
            {"strategy_id": strategy_id},
        ).all()
        gathered: dict[int, dict[str, int]] = {}
        for row in rows:
            gathered.setdefault(int(row.strategy_id), {})[str(row.status)] = int(row.runs)
        return {key: RunCounts(**counts) for key, counts in gathered.items()}

    def _sweep_counts(self, session: Session, strategy_id: int | None = None) -> dict[int, int]:
        clause = "" if strategy_id is None else " WHERE strategy_id = :strategy_id"
        return {
            int(row.strategy_id): int(row.sweeps)
            for row in session.execute(
                text(
                    f"SELECT strategy_id, count(*) AS sweeps FROM sweep{clause}"
                    " GROUP BY strategy_id"
                ),
                {"strategy_id": strategy_id},
            ).all()
        }

    def _last_finished(
        self, session: Session, strategy_id: int | None = None
    ) -> dict[int, datetime]:
        clause = "" if strategy_id is None else " AND strategy_id = :strategy_id"
        return {
            int(row.strategy_id): row.last
            for row in session.execute(
                text(
                    "SELECT strategy_id, max(finished_at) AS last FROM run"
                    f" WHERE finished_at IS NOT NULL{clause}"
                    " GROUP BY strategy_id"
                ),
                {"strategy_id": strategy_id},
            ).all()
        }

    def _best_runs(self, session: Session, strategy_ids: Sequence[int]) -> dict[int, RunRow]:
        """The best walk-forward result per strategy, by Sharpe.

        Walk-forward only, and that is the whole point of the field: the plan
        says the leaderboard sorts on the walk-forward number, and a strategy's
        headline on the list screen is the same claim in one line. A best-of
        that ranged over in-sample runs would put the luckiest member of the
        widest sweep on the front page, which is the exact failure Phase 6
        exists to prevent.
        """
        if not strategy_ids:
            return {}
        rows = session.execute(
            text(
                """
                SELECT DISTINCT ON (r.strategy_id) r.strategy_id, r.id
                  FROM run r
                  JOIN run_metric m ON m.run_id = r.id AND m.name = :sort
                 WHERE r.status = 'succeeded'
                   AND r.oos_start IS NOT NULL
                   AND r.strategy_id = ANY(:ids)
                 ORDER BY r.strategy_id, m.value DESC, r.id
                """
            ),
            {"sort": DEFAULT_SORT, "ids": list(strategy_ids)},
        ).all()
        if not rows:
            return {}
        by_run = {int(row.id): int(row.strategy_id) for row in rows}
        return {
            by_run[row.id]: row for row in self._rows(session, list(by_run)) if row.id in by_run
        }

    def _sweeps(
        self,
        session: Session,
        strategy_id: int | None = None,
        sweep_id: int | None = None,
    ) -> tuple[SweepRow, ...]:
        clauses: list[str] = []
        if strategy_id is not None:
            clauses.append("sw.strategy_id = :strategy_id")
        if sweep_id is not None:
            clauses.append("sw.id = :sweep_id")
        where = f" WHERE {' AND '.join(clauses)}" if clauses else ""
        rows = session.execute(
            text(
                """
                SELECT sw.id, sw.name, sw.seeded, sw.created_at, sw.cancelled_at,
                       sw.total_runs, sw.trials, sw.spec, sw.aerie_revision,
                       st.name AS strategy
                  FROM sweep sw
                  JOIN strategy st ON st.id = sw.strategy_id
                """
                # The progress counts come from the runs themselves rather than
                # from a status column on the sweep - there isn't one, on
                # purpose (db/models.Sweep): a second place recording whether a
                # batch finished is a second place it can be wrong.
                f"{where} ORDER BY sw.created_at DESC, sw.id DESC"
            ),
            {"strategy_id": strategy_id, "sweep_id": sweep_id},
        ).all()
        if not rows:
            return ()

        ids = [int(row.id) for row in rows]
        progress: dict[int, dict[str, int]] = {}
        for entry in session.execute(
            text(
                "SELECT sweep_id, status, count(*) AS runs FROM run"
                " WHERE sweep_id = ANY(:ids) GROUP BY sweep_id, status"
            ),
            {"ids": ids},
        ).all():
            progress.setdefault(int(entry.sweep_id), {})[str(entry.status)] = int(entry.runs)

        return tuple(
            SweepRow(
                id=int(row.id),
                name=str(row.name),
                strategy=str(row.strategy),
                seeded=bool(row.seeded),
                created_at=row.created_at,
                cancelled_at=row.cancelled_at,
                trials=int(row.trials),
                spec=row.spec,
                aerie_revision=row.aerie_revision,
                progress=_progress(
                    int(row.total_runs),
                    bool(row.cancelled_at is not None),
                    progress.get(int(row.id), {}),
                ),
            )
            for row in rows
        )

    def _trades(self, session: Session, run_id: int) -> tuple[TradeView, ...]:
        return tuple(
            TradeView(
                sequence=int(row.sequence),
                symbol=str(row.symbol),
                filled_at=row.filled_at,
                quantity=int(row.quantity),
                price=row.price,
                reference_price=row.reference_price,
                commission=row.commission,
                realized_pnl=row.realized_pnl,
                tag=str(row.tag),
            )
            for row in session.execute(
                text(
                    """
                    SELECT t.sequence, i.symbol, t.filled_at, t.quantity, t.price,
                           t.reference_price, t.commission, t.realized_pnl, t.tag
                      FROM trade t
                      JOIN instrument i ON i.id = t.instrument_id
                     WHERE t.run_id = :run_id
                     ORDER BY t.sequence
                    """
                ),
                {"run_id": run_id},
            ).all()
        )

    def _curve(self, session: Session, run_id: int) -> CurveView:
        row = session.execute(
            text("SELECT points, points_total, sampled FROM run_curve WHERE run_id = :run_id"),
            {"run_id": run_id},
        ).one_or_none()
        if row is None:
            # Not an error and not an empty curve: a run that finished before
            # curves were stored has none, and saying so is the difference
            # between "this run did nothing" and "nobody wrote it down".
            return CurveView(recorded=False)
        points: Sequence[Sequence[str]] = row.points
        return CurveView(
            recorded=True,
            points=tuple((datetime.fromisoformat(point[0]), Decimal(point[1])) for point in points),
            points_total=int(row.points_total),
            sampled=bool(row.sampled),
        )

    def _folds(self, session: Session, run_id: int) -> tuple[FoldView, ...]:
        return tuple(
            FoldView(
                fold=int(row.fold),
                train_start=row.train_start,
                train_end=row.train_end,
                test_start=row.test_start,
                test_end=row.test_end,
                params=row.params,
                candidates=int(row.candidates),
                train_objective=row.train_objective,
                starting_cash=row.starting_cash,
                ending_equity=row.ending_equity,
            )
            for row in session.execute(
                text(
                    """
                    SELECT f.fold, f.train_start, f.train_end, f.test_start, f.test_end,
                           f.candidates, f.train_objective, f.starting_cash, f.ending_equity,
                           ps.params AS params
                      FROM walk_forward_fold f
                      JOIN param_set ps ON ps.id = f.param_set_id
                     WHERE f.run_id = :run_id
                     ORDER BY f.fold
                    """
                ),
                {"run_id": run_id},
            ).all()
        )


def parameters_of(model: type[Params]) -> tuple[ParameterView, ...]:
    """A strategy's parameter space, read off its own pydantic model.

    Both halves of the space, in declaration order: the parameters a sweep can
    walk, with the range and step they declared, and the ones it cannot, which
    are configuration for a run. The launcher needs both - it offers a range
    for the first and a value for the second - and a view that returned only
    the swept ones would make an unswept parameter invisible rather than fixed.
    """
    space = model.sweep_space()
    views: list[ParameterView] = []
    for name, info in model.model_fields.items():
        bounds = space.get(name)
        default = info.get_default(call_default_factory=True)
        views.append(
            ParameterView(
                name=name,
                description=info.description or "",
                default=None if default is PydanticUndefined else str(default),
                swept=bounds is not None,
                low=None if bounds is None else bounds.low,
                high=None if bounds is None else bounds.high,
                step=None if bounds is None else bounds.step,
                count=None if bounds is None else bounds.count,
            )
        )
    return tuple(views)


def _progress(total_runs: int, cancelled: bool, by_status: Mapping[str, int]) -> SweepProgressView:
    finished = sum(
        count
        for status, count in by_status.items()
        if RunStatus(status).is_terminal  # the queue's own definition, not a second list
    )
    return SweepProgressView(
        total_runs=total_runs,
        finished=finished,
        by_status=dict(by_status),
        # A cancelled sweep is complete when its runs stop moving, which is
        # what `finished >= total_runs` says: cancellation marks the queued
        # rows cancelled, and the ones already in flight finish.
        complete=finished >= total_runs,
        cancelled=cancelled,
    )


def _bounded(limit: int) -> int:
    """A caller's limit, clamped rather than refused.

    Refusing would make a client's ``?limit=1000`` an error page instead of a
    board; clamping makes it a board of ``MAX_LIMIT`` rows, which is what the
    client wanted a version of.
    """
    return max(1, min(int(limit), MAX_LIMIT))


def _total(counts: Iterable[RunCounts]) -> RunCounts:
    """Per-strategy counts, added up. The whole queue's depth."""
    gathered = RunCounts()
    for entry in counts:
        gathered = RunCounts(
            queued=gathered.queued + entry.queued,
            running=gathered.running + entry.running,
            succeeded=gathered.succeeded + entry.succeeded,
            failed=gathered.failed + entry.failed,
            cancelled=gathered.cancelled + entry.cancelled,
        )
    return gathered
