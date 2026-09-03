"""``python -m aerie_trading.runs`` - the worker, and the sweep launcher.

One entry point with subcommands, for the reason ``collect/__main__.py`` gives:
the container's ``command:`` names something that exists in the source tree and
runs identically outside the image.

===========  ==============================================================
``work``     the worker loop. What the worker Deployment runs.
``plan``     expand a sweep and print what it would enqueue. Writes nothing.
``enqueue``  the same expansion, written to the queue, against a confirmed size.
``demo``     enqueue the named demo sweep (``runs/demo.py``).
``status``   queue depth, and one sweep's progress.
``cancel``   stop a sweep's queued runs.
===========  ==============================================================

**Exit codes are the interface with Kubernetes**, same as the collector's:

===  =======================================================================
0    it worked.
1    it failed.
2    it was **refused** - the confirmed size did not match the plan, or the
     plan exceeded the ceiling. Distinct from 1 because a refusal is this
     program working correctly, and because a seed Job that exits 2 has a
     different remedy from one that exits 1.
===  =======================================================================

**``plan`` and ``enqueue`` are two commands rather than a flag**, and that is
the estimate-and-confirm handshake the plan asks for made into something an
operator cannot skip by not reading: ``enqueue`` requires ``--confirm N``, and
the only way to learn N is to run ``plan``. ``demo`` is the one exception and
says so in ``runs/demo.py``: its size is a decision made once in code, so the
code confirms it.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
from collections.abc import Sequence
from datetime import UTC, datetime
from decimal import Decimal, InvalidOperation
from pathlib import Path

from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.db.engine import create_ledger_engine
from aerie_trading.lake.reader import LakeReader
from aerie_trading.logging import configure_logging
from aerie_trading.providers.base import Interval
from aerie_trading.revision import read_revision
from aerie_trading.runs.costs import CostSpec
from aerie_trading.runs.demo import demo_sweeps
from aerie_trading.runs.queue import RunQueue, cancel_sweep, read_queue_depth, sweep_progress
from aerie_trading.runs.sweep import (
    SweepNotConfirmed,
    SweepPlan,
    SweepSpec,
    SweepTooLarge,
    enqueue_sweep,
    full_grid,
    plan_sweep,
)
from aerie_trading.runs.worker import Assessor, HistoryCache, Worker, default_worker_name
from aerie_trading.settings import Settings, get_settings
from aerie_trading.strategies import spec_for

logger = logging.getLogger(__name__)

#: Exit code for a refused enqueue. See the module docstring.
EXIT_REFUSED = 2


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="python -m aerie_trading.runs",
        description="Run backtests off the queue, and put them on it.",
    )
    subcommands = parser.add_subparsers(dest="command", required=True)

    work = subcommands.add_parser("work", help="Claim and execute runs until stopped.")
    work.add_argument(
        "--max-runs",
        type=int,
        help="Stop after this many runs. Unbounded by default, which is what a Deployment wants.",
    )
    work.add_argument(
        "--drain",
        action="store_true",
        help="Exit when the queue is empty instead of polling. For draining by hand.",
    )

    for name, help_text in (
        ("plan", "Expand a sweep and print what it would enqueue. Writes nothing."),
        ("enqueue", "Expand a sweep and write it to the queue."),
    ):
        command = subcommands.add_parser(name, help=help_text)
        _add_spec_arguments(command)
        if name == "enqueue":
            command.add_argument(
                "--confirm",
                type=int,
                required=True,
                help="The run count `plan` reported. Refused (exit 2) if it does not match.",
            )

    demo = subcommands.add_parser("demo", help="Enqueue the named demo sweep.")
    demo.add_argument(
        "--dry-run",
        action="store_true",
        help="Print what the demo would enqueue and write nothing.",
    )

    status = subcommands.add_parser("status", help="Queue depth, and one sweep's progress.")
    status.add_argument("--sweep", type=int, help="Also report this sweep's progress.")

    cancel = subcommands.add_parser("cancel", help="Cancel a sweep's queued runs.")
    cancel.add_argument("--sweep", type=int, required=True)

    return parser


def _add_spec_arguments(command: argparse.ArgumentParser) -> None:
    """The two ways to name a sweep: a JSON spec, or flags that build one."""
    command.add_argument(
        "--spec",
        type=Path,
        help=(
            "A JSON file holding a SweepSpec. Every other flag below is ignored."
            " This is the round trip a control panel uses; the flags are for a person."
        ),
    )
    command.add_argument("--name", help="What to call this sweep.")
    command.add_argument("--strategy", help="A registered strategy name.")
    command.add_argument(
        "--sweep",
        action="append",
        dest="swept",
        default=[],
        metavar="PARAM",
        help=(
            "Walk this parameter over the range it declares, at the step it declares."
            " Repeatable. The declared range is the one the model validates against,"
            " so a sweep cannot produce a value the strategy would refuse."
        ),
    )
    command.add_argument(
        "--set",
        action="append",
        dest="explicit",
        default=[],
        metavar="PARAM=V1,V2",
        help="Walk this parameter over exactly these values instead. Repeatable.",
    )
    command.add_argument(
        "--symbol",
        action="append",
        dest="symbols",
        default=[],
        help="A symbol in the run's universe. Repeatable. Defaults to the collected symbols.",
    )
    command.add_argument(
        "--interval",
        choices=tuple(interval.value for interval in Interval),
        default=Interval.ONE_DAY.value,
    )
    command.add_argument("--start", type=_utc_date, help="Window start (inclusive), ISO date.")
    command.add_argument("--end", type=_utc_date, help="Window end (exclusive), ISO date.")
    command.add_argument("--cash", type=Decimal, default=Decimal(100_000))
    command.add_argument(
        "--slippage-bps",
        type=Decimal,
        help="Override the default cost model's slippage. See runs/costs.py.",
    )
    command.add_argument("--priority", type=int, default=100, help="Lowest first.")


def _utc_date(value: str) -> datetime:
    """An ISO date on the command line, as a UTC instant.

    A date rather than a timestamp because a sweep window is a range of
    sessions, and asking an operator for an instant invites one without a
    timezone - which ``SweepSpec`` refuses, correctly, at the point where the
    useful message is harder to produce.
    """
    return datetime.fromisoformat(value).replace(tzinfo=UTC)


def main(argv: Sequence[str] | None = None) -> int:
    arguments = build_parser().parse_args(argv)
    settings = get_settings()
    configure_logging(settings.log_level)

    if arguments.command == "work":
        return _work(arguments, settings)
    if arguments.command == "status":
        return _status(arguments, settings)
    if arguments.command == "cancel":
        return _cancel(arguments, settings)
    return _launch(arguments, settings)


# -- the worker --------------------------------------------------------------


def _work(arguments: argparse.Namespace, settings: Settings) -> int:
    config = settings.runs
    name = config.worker_name or default_worker_name()
    revision = read_revision().revision
    engine = create_ledger_engine(settings, pool_size=2)
    logger.info(
        "Worker starting",
        extra={"Worker": name, "AerieRevision": revision, "LakeRoot": str(settings.lake_root)},
    )
    try:
        with LakeReader(settings.lake_root) as reader:
            cache = HistoryCache(reader, maxsize=config.history_cache)
            worker = Worker(
                queue=RunQueue(
                    engine,
                    worker=name,
                    lease_seconds=config.lease_seconds,
                    retry_backoff_seconds=config.retry_backoff_seconds,
                ),
                cache=cache,
                revision=revision,
                config=config,
                # The composition root is where "a broad index" stops being an
                # abstraction: an installation that named one gets it, and one
                # that did not gets everything it collects bars for, which is
                # the broadest thing this lake has. Resolved here rather than
                # inside the Assessor because `settings.collection` is the
                # installation's answer and the honesty layer should not be
                # reaching for it.
                assessor=Assessor(
                    cache,
                    settings.honesty,
                    settings.honesty.index_symbols or settings.collection.bar_symbols,
                ),
            )
            worker.install_signal_handlers()
            report = worker.run(
                max_runs=arguments.max_runs,
                # One idle poll is enough to know the queue is empty: the claim
                # that returned nothing was a statement against the database,
                # not a guess.
                max_idle_polls=1 if arguments.drain else None,
            )
    finally:
        engine.dispose()

    logger.info(
        "Worker stopped",
        extra={
            "Worker": name,
            "Claimed": report.claimed,
            "Succeeded": report.succeeded,
            "Failed": report.failed,
            "Discarded": report.discarded,
        },
    )
    return 0


# -- launching ---------------------------------------------------------------


def _launch(arguments: argparse.Namespace, settings: Settings) -> int:
    if arguments.command == "demo":
        # Over what this installation collects, rather than over the constants'
        # own universe - see runs/demo.demo_sweeps.
        specs = demo_sweeps(settings.collection.bar_symbols, settings.honesty.walk_forward)
        dry_run = bool(arguments.dry_run)
        confirmations = [None] * len(specs)
    else:
        specs = (_spec_from(arguments, settings),)
        dry_run = arguments.command == "plan"
        confirmations = [getattr(arguments, "confirm", None)]

    plans: list[SweepPlan] = []
    for spec in specs:
        plan = plan_sweep(spec)
        plans.append(plan)
        print(plan.describe())

    if dry_run:
        if len(plans) == 1:
            print(f"Nothing was enqueued. Confirm with --confirm {plans[0].total}.")
        else:
            print("Nothing was enqueued.")
        return 0

    # The handshake is checked here, before a connection is opened. Refusing an
    # unconfirmed sweep is this program working correctly, and a correct
    # refusal that first needs a database to be reachable is one an operator
    # cannot get on the day the database is the thing that is wrong.
    # `enqueue_sweep` checks it again, because that is the function holding the
    # invariant and it has callers other than this one.
    for plan, confirmed in zip(plans, confirmations, strict=True):
        try:
            _refuse_early(plan, confirmed, settings.runs.max_sweep_runs)
        except (SweepNotConfirmed, SweepTooLarge) as refusal:
            logger.error("Sweep refused: %s", refusal)
            print(refusal)
            return EXIT_REFUSED

    engine = create_ledger_engine(settings, pool_size=2)
    try:
        return _enqueue_all(engine, settings, plans, confirmations)
    finally:
        engine.dispose()


def _refuse_early(plan: SweepPlan, confirmed: int | None, ceiling: int) -> None:
    """The two refusals that need no database, raised before one is opened."""
    if confirmed is not None and confirmed != plan.total:
        raise SweepNotConfirmed(plan.total, confirmed)
    if plan.total > ceiling:
        raise SweepTooLarge(plan.total, ceiling)


def _enqueue_all(
    engine: Engine,
    settings: Settings,
    plans: Sequence[SweepPlan],
    confirmations: Sequence[int | None],
) -> int:
    # Imported here rather than at module scope: this is the one code path in
    # the package that needs a market data provider, and building one drags in
    # exchange_calendars and therefore pandas - 142 MB a worker pod must not
    # pay for because a launcher in the same package wanted a `data_source` id.
    #
    # It comes from the collector's composition root rather than being built
    # here, so Phase 8's "add Schwab" is still one branch in one function.
    from aerie_trading.collect.__main__ import build_provider
    from aerie_trading.providers.registry import ensure_data_source

    revision = read_revision().revision
    provider = build_provider(settings)

    # One transaction for every sweep in the batch. The demo enqueues a grid
    # and the baseline it is measured against, and a leaderboard holding the
    # grid without the baseline is the half-seeded state Phase 7 would then
    # have to render.
    with Session(engine) as session, session.begin():
        source = ensure_data_source(session, provider)
        session.flush()
        for plan, confirmed in zip(plans, confirmations, strict=True):
            try:
                sweep_id = enqueue_sweep(
                    session,
                    plan,
                    confirm=plan.total if confirmed is None else confirmed,
                    data_source_id=source.id,
                    revision=revision,
                    ceiling=settings.runs.max_sweep_runs,
                )
            except (SweepNotConfirmed, SweepTooLarge) as refusal:
                # Rolled back by the context manager on the way out, so a
                # refused second sweep does not leave the first one enqueued.
                logger.error("Sweep refused: %s", refusal)
                return EXIT_REFUSED
            print(f"Enqueued {plan.total} run(s) as sweep {sweep_id} ({plan.spec.name}).")
    return 0


def _spec_from(arguments: argparse.Namespace, settings: Settings) -> SweepSpec:
    """A ``SweepSpec`` from a file, or built from the flags."""
    if arguments.spec is not None:
        return SweepSpec.model_validate(json.loads(Path(arguments.spec).read_text("utf-8")))

    if not arguments.strategy:
        raise SystemExit("--strategy is required unless --spec names a file")
    if arguments.start is None or arguments.end is None:
        raise SystemExit("--start and --end are required unless --spec names a file")

    strategy = spec_for(arguments.strategy)
    grid: dict[str, tuple[Decimal, ...]] = dict(full_grid(strategy, arguments.swept))
    for entry in arguments.explicit:
        name, _, values = str(entry).partition("=")
        if not name or not values:
            raise SystemExit(f"--set wants PARAM=V1,V2 and got {entry!r}")
        grid[name] = _decimals(name, values)

    costs = (
        CostSpec()
        if arguments.slippage_bps is None
        else CostSpec(slippage_bps=arguments.slippage_bps)
    )
    return SweepSpec(
        name=arguments.name or f"{arguments.strategy}-{datetime.now(UTC):%Y%m%d-%H%M%S}",
        strategy=arguments.strategy,
        grid=grid,
        symbols=tuple(arguments.symbols) or settings.collection.bar_symbols,
        interval=Interval(arguments.interval),
        window_start=arguments.start,
        window_end=arguments.end,
        starting_cash=arguments.cash,
        costs=costs,
        priority=arguments.priority,
        # The installation's default, baked into the spec at launch. See
        # honesty/config.py on why the sweep carries it instead of a worker
        # reading it back: a fold count that changed under a finished result
        # would relabel every number already on the leaderboard.
        walk_forward=settings.honesty.walk_forward,
    )


def _decimals(name: str, values: str) -> tuple[Decimal, ...]:
    try:
        return tuple(Decimal(entry.strip()) for entry in values.split(",") if entry.strip())
    except InvalidOperation:
        raise SystemExit(f"--set {name} wants numbers and got {values!r}") from None


# -- looking at it -----------------------------------------------------------


def _status(arguments: argparse.Namespace, settings: Settings) -> int:
    engine = create_ledger_engine(settings, pool_size=2)
    try:
        for depth in read_queue_depth(engine):
            print(f"{depth.status:>10}: {depth.runs}")
        if arguments.sweep is not None:
            progress = sweep_progress(engine, arguments.sweep)
            counts = ", ".join(
                f"{status} {count}" for status, count in sorted(progress.by_status.items())
            )
            state = " (cancelled)" if progress.cancelled else ""
            print(
                f"sweep {progress.sweep_id} {progress.name}{state}:"
                f" {progress.finished}/{progress.total_runs} finished [{counts}]"
            )
    finally:
        engine.dispose()
    return 0


def _cancel(arguments: argparse.Namespace, settings: Settings) -> int:
    engine = create_ledger_engine(settings, pool_size=2)
    try:
        cancelled = cancel_sweep(engine, arguments.sweep)
    finally:
        engine.dispose()
    # Said plainly rather than reported as a total, because runs already in
    # flight are deliberately allowed to finish - see runs/queue.cancel_sweep.
    print(f"Cancelled {cancelled} queued run(s). Runs already in flight will finish.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
