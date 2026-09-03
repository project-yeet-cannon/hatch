"""``python -m aerie_trading.seed`` - what the seed container runs on deploy.

A module rather than a console script, for the reason every entry point in this
package is one: the manifest's ``command:`` names something that exists in the
source tree and runs identically outside the image.

**Exit codes are the interface with Kubernetes**, matching the collector's and
the runner's:

===  =======================================================================
0    the seed ran. It may have done nothing, which is the correct outcome on
     every deploy after the first.
1    it failed. Nothing partial is left behind: each step is its own
     transaction, and the sweeps are one transaction for the batch.
===  =======================================================================

There is deliberately no exit code for *"it did nothing"*. This runs on every
deploy, and a job that went yellow on the second one would train an operator to
ignore it by the fourth.

``--dry-run`` reports what the seed would do and writes nothing anywhere -
neither rows nor Parquet - which is what makes "will this deploy touch anything"
a question an operator can ask before answering it with a deploy.
"""

from __future__ import annotations

import argparse
import logging
import sys
from collections.abc import Sequence

from aerie_trading.db.engine import create_ledger_engine
from aerie_trading.logging import configure_logging
from aerie_trading.revision import read_revision
from aerie_trading.runs.demo import demo_sweeps
from aerie_trading.runs.sweep import plan_sweep
from aerie_trading.seed.seeding import seed
from aerie_trading.settings import get_settings

logger = logging.getLogger(__name__)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="python -m aerie_trading.seed",
        description="Register the shipped strategies and enqueue the demo sweeps.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print what would be seeded and write nothing.",
    )
    parser.add_argument(
        "--no-collect",
        action="store_true",
        help=(
            "Do not backfill the demo window into the Lake."
            " For an installation whose history arrives another way."
        ),
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    arguments = build_parser().parse_args(argv)
    settings = get_settings()
    configure_logging(settings.log_level)

    # Built here, in the composition root, exactly like the collector's - and
    # this is the only process in the seed that needs one. Phase 8's "add
    # Schwab" stays one branch in one function.
    from aerie_trading.collect.__main__ import build_provider

    provider = build_provider(settings)
    specs = demo_sweeps(settings.collection.bar_symbols, settings.honesty.walk_forward)

    if arguments.dry_run:
        for spec in specs:
            print(plan_sweep(spec).describe())
        print("Nothing was written.")
        return 0

    engine = create_ledger_engine(settings, pool_size=2)
    try:
        report = seed(
            engine,
            settings,
            provider,
            revision=read_revision().revision,
            collect=not arguments.no_collect,
        )
    finally:
        engine.dispose()

    print(f"Registered {report.strategies} strategy(ies).")
    print(
        f"Collected {report.bars} bar(s)."
        if report.bars
        else "The Lake already covered the demo window."
    )
    if report.sweeps:
        print(f"Enqueued: {', '.join(report.sweeps)}.")
    if report.existing:
        print(f"Already seeded, left alone: {', '.join(report.existing)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
