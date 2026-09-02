# Aerie.Trading

A strategy laboratory that rides Aerie as a platform. The plan is
[`docs/plans/trading.md`](../../docs/plans/trading.md); this file is only the
part you need to run it.

At Phase 1 this is a control plane with nothing to control: a FastAPI service
that can say whether it is alive, whether it can reach its database, what
commit it was built from, and what its metrics are. That is the point — the
phase is judged on whether the deploy pipeline, the logs, the metrics and the
ingress all work, and that is only checkable while there is nothing
interesting behind them.

Phases 2 and 3 wait on a Schwab approval measured in business days, and the day
it lands should be spent writing a collector, not a `Dockerfile`.

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

## How it deploys

The image is `Dockerfile.trading`, built by
[`publish.yml`](../../.github/workflows/publish.yml) whenever this directory
changes, with the build context set to *this directory* rather than the
repository root — which is the extraction seam expressed in the build, since
Docker refuses a `COPY` that escapes its context.

The manifests are [`deploy/cluster/trading/`](../../deploy/cluster/trading/):
a CNPG `Cluster` with its own WAL destination and nightly base backup, and a
`Deployment` whose init container runs `alembic upgrade head` before the
service starts. `deploy/cluster/trading.yaml` splits those into two Flux
Kustomizations so the migration never runs against a database that is not up
yet.

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
| `aerie_trading/providers/` | Phase 2 — `MarketDataProvider` and its Schwab implementation |
| `aerie_trading/collect/` | Phase 3 — the bar and option-chain collectors |
| `aerie_trading/engine/` | Phase 4 — clock, instruments, portfolio, broker, strategy |
| `aerie_trading/strategies/` | Phase 4 on — one strategy per module |

Type checking is `pyright` in **strict** mode, deliberately: the owner does not
write Python, and pydantic models under a strict checker read much like C#. If
strict mode is ever loosened, that is a decision to make in the open — in
`pyproject.toml`, with a reason next to it.
