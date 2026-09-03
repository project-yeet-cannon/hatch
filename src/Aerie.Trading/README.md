# Aerie.Trading

A strategy laboratory that rides Aerie as a platform. The plan is
[`docs/plans/trading.md`](../../docs/plans/trading.md); this file is only the
part you need to run it.

At Phase 3 this is a control plane, a market that does not exist, and a lake
filling up with it. The service can say whether it is alive, whether it can
reach its database, what commit it was built from and what its metrics are -
including, now, how every collector is doing. Beside it is a
`MarketDataProvider` interface and a generator that implements it, so every
phase after this one has bars and option chains to work on without a credential,
a network call or a market being open; and beside *that* is the machinery that
turns a provider into a queryable history: a partition layout, idempotent
writes, a DuckDB reader, two collectors and the CronJobs that run them.

Nothing here waits on Schwab. That is the plan's re-cut: the collector, the
lake, the engine and the sweeps are all built and hardened against the synthetic
source, and Phase 8 becomes a second provider class dropped into machinery that
already works.

## Why this is a silo

It is Python because options are: QuantLib and `py_vollib` for pricing and
implied-vol solving, and `exchange_calendars` for the market calendar that
knows every NYSE half-day forward and back. A wrong market calendar does not
throw — it silently corrupts every backtest that crosses a holiday, and writing
a worse version of a solved problem in C# is the alternative.

It is *separate* because it may leave. The extraction seam is
`src/Aerie.Trading/` plus [`deploy/cluster/trading/`](../../deploy/cluster/trading/)
plus the SPA, and
nothing else crosses the line:

- nothing here imports from the other side, no `.csproj` references this
  directory, no path in it points outside it, and the image's build context is
  this directory — so Docker itself refuses a `COPY` across the line. The
  `trading-boundary` job in [`ci.yml`](../../.github/workflows/ci.yml) asserts
  all of it on every commit;
- `make trading-test` is its own target, and the trading lane in
  [`ci.yml`](../../.github/workflows/ci.yml) is its own job. A Python failure
  is never a red .NET build.

What it *does* consume is the platform: Postgres as an operator, GitOps deploy,
ingress behind the auth wall, log shipping, metrics, and backups. The app's
side of that contract is a Flux kustomization, which is the same contract a
stranger's sideloaded service would use.

## Running it

Everything goes through [uv](https://docs.astral.sh/uv/), which manages the
Python interpreter as well as the packages — there is no system Python to
install or virtualenv to activate.

```sh
# Once, if you don't have it. Also `brew install uv`.
curl -LsSf https://astral.sh/uv/install.sh | sh

# Lint, type-check and test, exactly as CI does.
make trading-test          # from the repository root
```

Piecemeal, from this directory:

```sh
uv sync                    # build .venv from uv.lock
uv run ruff format .       # format
uv run ruff check --fix .  # lint
uv run pyright             # type-check, strict
uv run pytest              # test
```

`uv.lock` is committed and `make trading-test` passes `--frozen`, so the local
run and the CI run resolve to identical versions. Adding a dependency is
`uv add <package>`, which updates `pyproject.toml` and the lockfile together;
commit both.

**One part of the suite needs a Postgres, and skips without one.** The run
queue is a Postgres queue — `SELECT … FOR UPDATE SKIP LOCKED`, a partial index,
a conditional update — and a fake implementing those in Python would be a test
of the fake. Those tests skip unless `TRADING_TEST_DATABASE_URL` names a
scratch database, which they will `TRUNCATE`; that is why it is an explicit
variable rather than a discovered default. CI's trading lane always sets it,
against a service container. Locally:

```sh
make trading-test-db       # the same suite, plus a throwaway Postgres on 55432
```

To run the service itself:

```sh
uv run python -m aerie_trading.control      # http://localhost:8080
```

It starts without a database and says so: `/healthz` answers 200 regardless,
`/readyz` answers 503 with `"database": "unreachable"` until one is reachable.
That split is not an accident of implementation — a liveness probe that checks
the database restarts every replica at once during a failover, which makes an
outage longer rather than shorter. Point it at a Postgres with libpq's own
variables if you want the other answer:

```sh
PGHOST=localhost PGDATABASE=trading PGUSER=trading PGPASSWORD=... \
  uv run python -m aerie_trading.control
```

| | |
|---|---|
| `GET /healthz` | liveness. Never touches the database |
| `GET /readyz` | readiness. 503 when the Ledger does not answer |
| `GET /metrics` | Prometheus. Carries `trading_build_info` |
| `GET /api/trading/version` | `revision`, `sequence`, `builtAt` — the same three field names as `GET /api/aerie-revision` on the .NET side |

`/metrics` also carries what the collectors and the run queue are doing, both
derived from the Ledger at scrape time rather than from counters: a CronJob pod
and a worker pod are both gone by the time Prometheus arrives, so the durable
row is the metric. `trading_collection_*` is collection health and
`trading_runs{status=…}` is queue depth, each beside an `up` gauge — a scrape
that could not reach the Ledger says so rather than failing, because a failed
scrape looks identical to a pod being down and would fire every alert at once
saying the wrong thing.

## How it deploys

The image is `Dockerfile.trading`, built by
[`publish.yml`](../../.github/workflows/publish.yml) whenever this directory
changes, with the build context set to *this directory* rather than the
repository root — which is the extraction seam expressed in the build, since
Docker refuses a `COPY` that escapes its context.

The manifests are [`deploy/cluster/trading/`](../../deploy/cluster/trading/):
a CNPG `Cluster` with its own WAL destination and nightly base backup, a
`Deployment` whose init container runs `alembic upgrade head` before the
service starts, and — from Phase 3 — a `lake/` directory holding the volume,
the collector CronJobs and a nightly restic backup of the option chains.
`deploy/cluster/trading.yaml` splits those into two Flux Kustomizations so the
migration never runs against a database that is not up yet.

The collectors are `CronJob`s rather than a scheduler process, and the reason
is in `deploy/cluster/trading/lake/cronjob-bars.yaml`: a missed run is a Job
that does not exist, which kube-state-metrics already reports, where a
scheduler's missed tick is invisible because the thing that would have logged
it was not running. The chain collector is *three* CronJobs, because cron
cannot say "every thirty minutes from 09:30 to 15:59" — that file has the
argument.

**One step lives outside this repository, and it is done.** The image tag
reaches the cluster as `${TRADING_IMAGE_TAG}`, from the site repo's
`aerie-image-tags` ConfigMap, and Flux's `ImageUpdateAutomation` can only
*move* a value next to an existing marker — it cannot add one. So
`image-tags.yaml` gained, once:

```yaml
TRADING_IMAGE_TAG: latest  # {"$imagepolicy": "flux-system:trading:tag"}
```

`latest` is a placeholder the `ImagePolicy` replaces with a real
`<timestamp>-<sha>` on its first scan after the image is published. Had the
line been missing, the `trading` Kustomization would fail its build under
strict substitution — contained to this layer by design, since nothing else in
the cluster depends on it.

## The synthetic market

`aerie_trading/providers/` is the interface every data source implements and
one implementation of it. There is no Schwab yet and nothing is waiting for
one.

```py
from datetime import datetime, timezone

from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

market = SyntheticMarketDataProvider()
market.bars(["ZVZZT"], Interval.ONE_DAY, start, end)
market.chain("ZVZZT", datetime(2026, 9, 1, 15, 0, tzinfo=timezone.utc))
```

Four things about it are worth knowing before you use it for anything.

**It is pure noise, permanently.** No implied-volatility surface, no jumps, no
regime switching, no microstructure, and none of those are coming. It is the
plan's null hypothesis — zero alpha by construction, asserted in
`tests/test_synthetic_zero_alpha.py` — which is what lets Phase 6 measure an
overfitting gate against a known correct answer. The moment it becomes
interesting it stops being a control.

**Its option prices are refused, in code.** The chains are structurally valid
and numerically meaningless: a delta of 0.62 on one strike and 0.31 on the next
does not mean the first is further in the money, they are independent draws that
happen to be adjacent in a ladder. `require_priceable_chains(provider)` raises
for this source, and the claim travels into the `data_source` row so the same
check works from the far side of the lake — failing closed when a row does not
say. Bars are a different matter and are a legitimate thing to backtest against.

**A value is a function of its coordinates, not of the request.** The same
request returns the same bar forever, in this process or another one, on this
architecture or the image's. Nothing is persisted and there is no cursor, which
is what makes a backfill and an incremental collection of the same window agree
by construction.

**The universe is ZVZZT, ZWZZT, ZXZZT, ZBZZT and ZJZZT** — Nasdaq's reserved
test tickers, so that no row, screenshot or leaderboard entry can be mistaken
for a claim about a real instrument. `ZJZZT` deliberately has no options board.
Re-seed or replace it from the environment without a rebuild:

```sh
TRADING_SYNTHETIC__SEED=4242 uv run python -m aerie_trading.control
TRADING_SYNTHETIC='{"universe": [{"symbol": "ZVZZT", "start_price": 42.0}]}' ...
```

The market calendar is real even though the prices are not: `XNYS` via
`exchange_calendars`, with its holidays and its early closes, because a
synthetic session that ran 24/7 would leave Phase 3's calendar handling — the
one piece of it that fails silently rather than loudly — completely
unexercised. Asking for a quote or a chain on a holiday raises rather than
returning nothing, since an empty answer is indistinguishable from a market
that was open and silent.

## The two things that look like details and are not

**The log shape.** `aerie_trading/logging.py` reproduces .NET's
`AddJsonConsole` output field for field, because fluent-bit parses container
stdout with a parser named `dotnet_json` and then derives `service` and
`aerie_revision` from specific keys
([`service_tag.lua`](../../deploy/cluster/observability/controllers/fluent-bit/service_tag.lua)).
A line in a different shape still arrives in OpenSearch — as unparsed text,
missing exactly the two fields every saved search filters on. `State.Service`
is deliberately never set: its presence marks a line as *relayed*, which this
service's lines are not.

**The build stamp.** `aerie_trading/revision.py` reads a `build.json` written
into the package by the image build, not an environment variable set by the
manifest. A revision the deployment can supply is a revision the deployment can
get wrong, and everything that reads this value is trusting it to describe the
code that is actually running. A working tree has no `build.json`, so `uv run`
reports `dev`.

## The Lake

Parquet on a volume, read by DuckDB. Why it is not in Postgres is the plan's
own section; what you need to run it is that **the root is a config
parameter** — `TRADING_LAKE_ROOT`, which is `/lake` in the cluster and a
relative `.aerie-lake` outside it, so a pod whose volume failed to mount fails
visibly instead of quietly writing onto its own ephemeral filesystem.

The layout is fixed and is documented in `aerie_trading/lake/layout.py`,
because rewriting a partition scheme is a migration:

```
bars/{interval}/{symbol}/{year}/{month}.parquet
chains/{underlying}/{date}/{hhmm}.parquet
```

Two properties are worth knowing before you touch any of it:

**Writes are idempotent and crash-safe, by two different mechanisms.** A
re-collection replaces exactly the rows it names — an anti-join, not a
truncate, because a bar partition is a month and a collection is a session.
And every write goes to a temporary file in the destination's own directory
and is then renamed onto it, so a killed process leaves either the old file or
the new one and never a truncated Parquet footer.

**No caller learns the directory structure.** `lake/reader.py` takes a symbol
and a time range and hands back a polars frame; it names the files it will
open rather than globbing, which is what keeps "nothing was collected" an
empty frame instead of an IO error.

## Collecting

```sh
# One session, refusing a day the market was shut. What the CronJob runs.
uv run python -m aerie_trading.collect bars --mode incremental

# The most recent session, whenever it was. Catching up by hand.
uv run python -m aerie_trading.collect bars --mode latest

# A range, or the configured backfill depth from today.
uv run python -m aerie_trading.collect bars --mode backfill --start 2026-01-02

# The board, right now. Or a whole session's worth of snapshots at once,
# which only a provider that can answer for the past can satisfy.
uv run python -m aerie_trading.collect chains
uv run python -m aerie_trading.collect chains --session 2026-03-04
```

Every one of these writes an `ingest_run` row before it does any work, so they
need the Ledger. Exit codes are the interface with Kubernetes: `0` collected,
`1` failed, **`2` refused** — a holiday, a closed market. The third is distinct
because a run that exited `0` on Thanksgiving would advance the last-success
metric over a day nothing was collected.

What is collected is configuration, not code: `TRADING_COLLECTION__*` (or the
whole model as JSON in `TRADING_COLLECTION`) carries the watchlists, the bar
intervals, the snapshot cadence and the backfill depth.

## Runs, sweeps and the queue

A **strategy** is code and a deploy. A **variation** is data: a `param_set`
row. A **sweep** is a grid over a strategy's declared parameter ranges, and a
**run** is one backtest — which is also one row on the work queue, because the
queue *is* the `run` table.

```sh
# What would this sweep cost? Writes nothing.
uv run python -m aerie_trading.runs plan --strategy ma_crossover \
    --sweep fast --sweep slow --symbol ZVZZT \
    --start 2021-01-01 --end 2026-01-01

# The same thing, enqueued - and refused unless the count matches the estimate.
uv run python -m aerie_trading.runs enqueue --strategy ma_crossover \
    --sweep fast --sweep slow --symbol ZVZZT \
    --start 2021-01-01 --end 2026-01-01 --confirm 1725

# The named demo sweep, sized once in code (aerie_trading/runs/demo.py):
# nineteen crossovers plus the buy-and-hold baseline they are measured against,
# over the universe this installation collects.
uv run python -m aerie_trading.runs demo

# A worker. This is what the worker Deployment runs; --drain exits when empty.
uv run python -m aerie_trading.runs work --drain

uv run python -m aerie_trading.runs status --sweep 1
uv run python -m aerie_trading.runs cancel --sweep 1
```

`--sweep fast` walks the range **the strategy's own field declares** — there is
no range on the command line to disagree with the model. `--set fast=5,10,15`
gives explicit values instead. Either way the cross product is validated
through the strategy's `Params` model before anything is enqueued, so a corner
the strategy would refuse (`fast >= slow`) is pruned while planning rather than
after a worker has produced it.

**`plan` and `enqueue` are two commands rather than a flag**, and that is the
estimate-and-confirm handshake made into something that cannot be skipped by
not reading: `enqueue` needs `--confirm N`, and the only way to learn N is to
plan. A refusal exits **`2`** and happens before a database connection is
opened. `TRADING_RUNS__MAX_SWEEP_RUNS` is the other half — the ceiling that
does not depend on anyone reading the estimate.

**Losing a worker loses no runs and duplicates none**, and those are two
different mechanisms. A claim takes a *lease*; a lease that stops being renewed
is reclaimed and the run goes back on the queue, which is what stops work being
lost. A claim also mints a *fence token*, and a worker may only write a result
while its token is still the one on the row — so a "dead" worker that turns out
to have been merely frozen updates zero rows and discards its answer, which is
what stops work being written twice. A visibility timeout alone gives you the
first and quietly costs you the second.

Every run records the revision that produced it, a fingerprint of the bars it
read, and a fingerprint of the result. Two runs that disagree on the result and
agree on the data are a determinism bug; two that disagree on both were not run
over the same history, whatever their windows say.

Metrics are computed in exactly one place (`engine/metrics.py`) and stored one
row per metric, so Phase 6 adds names without a migration. **An undefined
metric is an absent row** — never zero and never `NaN`, because `NUMERIC` will
store `NaN` happily and Postgres sorts it above every number, which would put
one degenerate run at the top of a leaderboard sorted by Sharpe.

## The honesty layer

`aerie_trading/honesty/` adds no capability. Everything in it exists to keep a
number produced by the machinery above from being mistaken for a finding.

**Every sweep enqueues one extra run.** Beside the grid goes a
`kind = 'walk_forward'` row: the same grid, walked over rolling train/test
folds, with parameters chosen on each train window and one account carried
through the tests. It is one item of work on the same queue, so it leases,
retries and scales exactly like a backtest. Its window is out-of-sample by
construction, which is what makes it the only row in a sweep whose figures may
be labelled performance.

**A run with no out-of-sample window can never be a headline number**, and that
is enforced in the serializer rather than by convention: `figures_for` sorts a
run's metrics into `headline`, `in_sample` and `descriptive` by reading
`run.oos_start`, and constructing a `Figures` with anything in `headline` for a
run that has none raises `InSampleFigure` — which deliberately does not derive
from `ValueError`, so pydantic cannot fold it into an ordinary validation error
that a tidy `except` would swallow.

**Every result carries what it would have to beat.** The worker computes, and
stores beside each run:

| | |
|---|---|
| `baseline_return`, `excess_return` | buy-and-hold on the run's own universe, same window, same cash, same costs |
| `index_return`, `excess_return_index` | buy-and-hold on the broad universe — `TRADING_HONESTY__INDEX_SYMBOLS`, defaulting to everything this installation collects |
| `stressed_total_return`, `cost_sensitivity` | the same run re-scored with every cost rate multiplied by `TRADING_HONESTY__COST_STRESS_MULTIPLE` (5 by default) |
| `selection_trials`, `expected_max_sharpe`, `deflated_sharpe` | how many parameter sets were searched to find this one, what the best of that many earns by luck alone over this many bars, and the difference |

The baselines are memoized per history, so a thousand-run sweep pays for them
once rather than a thousand times. The cost re-score cannot be shared and
roughly doubles a sweep's compute; that is the price of *"a strategy that only
works at zero slippage is identified as such automatically"* being on by
default rather than opt-in.

**The gate is arithmetic rather than judgement**, because the synthetic source
has zero alpha by construction and asserts it. Sweeping it deliberately
produces the strongest false positive this machinery can manufacture, and
`tests/test_honesty_walk_forward.py` requires the honesty layer to catch it:
the best of fifty-seven crossovers beats the median of the same sweep by about
0.39 of a Sharpe — every point of which is selection — while the walk-forward
figure lands *below* that median and the selection-adjusted figure lands at or
below zero.

Worth knowing before reading those numbers: on this universe no crossover
posts a positive Sharpe at all, so the false positive is a *relative* one. The
gate is written against the gap rather than the sign deliberately — machinery
that only noticed inflated figures above zero would be blind to a leaderboard
sorted within one strategy, which is the common case.

## Layout

Directories arrive with the phase that needs them, so most of this is a map of
where things will go rather than of what is here:

| | |
|---|---|
| `aerie_trading/settings.py` | every value read from the environment, as one typed model |
| `aerie_trading/revision.py` | which commit this build came from |
| `aerie_trading/logging.py` | JSON on stdout, in the shape the cluster's log pipeline reads |
| `aerie_trading/control/` | FastAPI: health, readiness, metrics, version |
| `aerie_trading/db/` | the Ledger — SQLAlchemy models and the engine |
| `aerie_trading/migrations/` | Alembic, run from an init container on deploy |
| `tests/` | pytest, mirroring the package |
| `aerie_trading/providers/` | `MarketDataProvider`, the market calendar, and the synthetic source |
| `aerie_trading/lake/` | the Parquet lake — layout, schemas, idempotent writes, the DuckDB reader |
| `aerie_trading/collect/` | the bar and option-chain collectors, and the CLI a CronJob runs |
| `aerie_trading/engine/` | the backtester — clock, instruments, portfolio, broker, strategy, and the one place run metrics are computed |
| `aerie_trading/strategies/` | one strategy per module, plus the explicit registry |
| `aerie_trading/runs/` | sweeps, the Postgres work queue, and the worker loop |
| `aerie_trading/honesty/` | walk-forward folds, selection accounting, baselines and cost stress, and the serializer that refuses a fitted headline |

Type checking is `pyright` in **strict** mode, deliberately: the owner does not
write Python, and pydantic models under a strict checker read much like C#. If
strict mode is ever loosened, that is a decision to make in the open — in
`pyproject.toml`, with a reason next to it.
