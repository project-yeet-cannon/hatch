# Aerie.Trading

A strategy laboratory that rides Aerie as a platform. The plan is
[`docs/plans/trading.md`](../../docs/plans/trading.md); this file is only the
part you need to run it.

At Phase 0b there is no behavior here — a package with nothing in it, a
toolchain, and a lane in CI. That is the point: Phases 2 and 3 wait on a
brokerage approval measured in business days, and the day it lands should be
spent writing a collector, not a `pyproject.toml`.

## Why this is a silo

It is Python because options are: QuantLib and `py_vollib` for pricing and
implied-vol solving, and `exchange_calendars` for the market calendar that
knows every NYSE half-day forward and back. A wrong market calendar does not
throw — it silently corrupts every backtest that crosses a holiday, and writing
a worse version of a solved problem in C# is the alternative.

It is *separate* because it may leave. The extraction seam is
`src/Aerie.Trading/` plus `deploy/cluster/apps/trading/` plus the SPA, and
nothing else crosses the line:

- nothing here imports from `src/Aerie.Api/`, and no `.csproj` references this
  directory — Phase 1 adds the CI check that asserts it;
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

## Layout

Directories arrive with the phase that needs them, so most of this is a map of
where things will go rather than of what is here:

| | |
|---|---|
| `aerie_trading/` | the package. Empty at Phase 0b |
| `tests/` | pytest, mirroring the package |
| `aerie_trading/control/` | Phase 1 — FastAPI: health, readiness, metrics, version |
| `aerie_trading/providers/` | Phase 2 — `MarketDataProvider` and its Schwab implementation |
| `aerie_trading/collect/` | Phase 3 — the bar and option-chain collectors |
| `aerie_trading/engine/` | Phase 4 — clock, instruments, portfolio, broker, strategy |
| `aerie_trading/strategies/` | Phase 4 on — one strategy per module |

Type checking is `pyright` in **strict** mode, deliberately: the owner does not
write Python, and pydantic models under a strict checker read much like C#. If
strict mode is ever loosened, that is a decision to make in the open — in
`pyproject.toml`, with a reason next to it.
