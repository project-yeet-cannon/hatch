# Trading — a strategy laboratory that rides Aerie as a platform

**Status:** Not started. Phases 0–3 are on a **wall-clock critical path** and
should be kept deliberately lean: Schwab developer-app approval takes days, and
option-chain history cannot be backfilled at any sane price, so every day the
snapshotter is not running is a day of data that has to be bought later or done
without. Everything from Phase 4 on is ordinary engineering that can take the
time it takes.

## The ask

Verbatim, from the owner:

> We are going to develop a financial trading capability.
>
> Philosophy, thoughts, guiding principles, vision:
> - Evaluate lots of trading strategies, and lots of parameters for those strategies
> - Historical trading data is difficult to source but current prices are relatively easy. We need to build our own financial database to track markets
> - Trading stratgies will be presented in various formats - some may be published online, some may be my ideas, some may be provided as APIs or code or something
> - I want to run long-term simulations of trading strategies to gain some degree of confidence in them before going live
> - I want a control panel/admin interaction that shows all strategies as a top level interaction, with controls on how to run variations/parameters of those strategies. I want to gather results and metrics up in one place so it is easy for me to see at a glance what strategy has done well for a day or is doing the best at any particular time
> - We will not be using real money for a while so security is not a concern
> - i generally feel like the trading app should be its own silo because it might make sense to carve out into its own effort in the future. but it could be helpful to piggyback on existing projects too - we'll probably need an api/control plane, job running and aerie web api project has that already. i guess it's no big deal for you to set it up twice though. we will need to run compute jobs frequently and at various
>     - ultimately with productization of aerie, i think the trading source should be its own silo but i don't know the best method of telling aerie infra what topolgy the trading app needs
>     - maybe it should be its very own repo from the start? it's pretty orthogonal in purpose to what aerie does. it just plugs into the architecture and runs as a supported service with an SLA
>         - pontificating a little now that my mind is here - i think the heart of `aerie-the-product` is a push-button home cloud setup/deployment that provides desired home services meeting SLAs (primarily for uptime/availability and data backup/recoverability). i want to open source or sell the groundwork for setting up a home cluster, and sideload my personal stuff into my own home deployment. and allow other folks to customize their own as well.
> - we will need to run little compute jobs throughout the day,
> - Not HFT - We are technically sophisticated compared to the norm, but not compared to big financial actors. We are not racing for tiny temporal arbitrage. We're looking for strategic algorithms that are not leveraging such nichey areas
> - I won't be hooking money up to this for some time, so security, encryption, etc are not really concerns yet. We will do due diligence in time when we need to hook up to the financial system, but that is for the future if we find any promising strategies
> - I would generally expect to have to develop and deploy code to introduce a whole new strategy. We can probably simplify our ontology if we make that assumption now, which is fine.

## Decisions made with the owner up front

| Question | Decision |
|---|---|
| How siloed, physically? | **Its own project in this repo** — `src/Aerie.Trading/`, own process, own container, own database, own Flux app directory, and **no code dependency in either direction** with `Aerie.Api`. Carving it into its own repo later becomes a history split plus a CI workflow, not a rewrite |
| What language? | **Python for the whole silo**, control plane included. Driven by options: pricing, implied-vol solving, greeks and the exchange calendar are solved, validated and free in Python and absent in .NET. See [Why Python, specifically](#why-python-specifically) |
| Which markets? | **US equities and ETFs first, options as the actual target.** Equities validate the engine on free, simple data; the instrument model is options-shaped from day one so options are an addition rather than a rewrite |
| Which option strategies eventually? | **All three families** — premium selling, directional/long premium, and volatility. That makes greeks, expiry/assignment and multi-leg positions non-negotiable design inputs even though they are not Phase 1 code |
| Where does data come from? | **Schwab Trader API** — free real-time quotes and chains, from the same credential that eventually places orders. It requires a **Schwab retail brokerage account, which the owner does not have**; opening one is the first item of Phase 0a. Chosen over the account-free data vendors knowingly — see [Why Schwab, given it costs an account](#why-schwab-given-it-costs-an-account). The provider is an **interface**; Schwab is the first implementation, per [ethos.md](../ethos.md) |
| Where does market data live? | **Parquet on disk, queried in-process by DuckDB** — not Postgres. See [Why the lake is not in Postgres](#why-the-lake-is-not-in-postgres) |
| How big is the lake to start? | **A modest PVC on the default storage class**, sized for a handful of underlyings, with the lake root as a config parameter so relocating it is a value change. Not the reserved photos-extend space on node 2 |
| Backtest only, or live too? | **One engine, two clocks**, from the start. Promoting a backtested parameter set to paper trading is inserting a row, not porting code |
| How do strategies get authored? | **A Python class with a pydantic parameter schema.** A new strategy is code and a deploy; a *variation* is data. This is the ontology simplification the ask asks for |
| How does the silo tell Aerie what topology it needs? | **It ships a Flux kustomization.** GitOps already is that interface — see [The platform contract](#the-platform-contract) |

## What is there today

The silo is being sideloaded onto a platform that already exists. Nothing below
needs building; all of it needs *consuming*.

| Platform capability | What provides it | How trading uses it |
|---|---|---|
| Postgres as an operator | CNPG, [`deploy/cluster/data/`](../../deploy/cluster/data/) | Declares its **own** `Cluster`, separate database, separate backup schedule |
| Backup and restore verification | `scheduledbackup.yaml`, `verify-cronjob.yaml` | The Ledger inherits it; the Lake does **not** (see below) |
| GitOps deploy | Flux, [`deploy/cluster/apps/`](../../deploy/cluster/apps/) | One new app directory |
| Ingress + TLS + the auth wall | Traefik + middleware, [`charts/aerie/templates/`](../../charts/aerie/templates/) | `trading.${DOMAIN}` behind the same wall; the OAuth callback lands as an authenticated operator session |
| Log shipping | Fluent Bit → OpenSearch, [`deploy/cluster/observability/`](../../deploy/cluster/observability/) | Emit structured JSON on stdout and it is indexed for free |
| Metrics + dashboards | kube-prometheus-stack, same directory | `/metrics` endpoint; Grafana draws the equity curves |
| Alerting | Grafana + Uptime Kuma | Carries the Schwab token-expiry alarm — see Phase 2 |
| Design system | `@aerie/ui`, [`src/Aerie.Web/packages/ui/`](../../src/Aerie.Web/packages/ui/) | The control panel looks like Aerie for free |
| Commit binding | `aerie-revision`, [`version.md`](version.md) | Every run record stores the revision that produced it |

What trading explicitly does **not** consume: `Aerie.Api`'s process, its
`AerieContext`, its Quartz scheduler, its module system, or any of its C#. Those
are the couplings that would make extraction a rewrite.

## Design commitments

Five decisions that are cheap now and expensive later. Everything in the phases
below serves one of them.

### Why Python, specifically

Not because finance is Python-forward in general — because of options. Pricing
and implied-vol solving (QuantLib, py_vollib), and the exchange calendar
(`exchange_calendars`, which knows every NYSE half-day and early close forward
and back) are solved and validated in Python and have no equivalent in .NET. A
wrong market calendar does not throw; it silently corrupts every backtest that
crosses a holiday. Writing a worse version of a solved problem in C# is the
alternative, and it is not a good one.

Two costs, accepted knowingly:

- **The owner does not write Python.** Mitigated by strict typing — pydantic
  models plus `pyright` in strict mode read much like C#, and the checker runs
  in CI. The silo boundary means a Python problem never becomes an Aerie
  problem.
- **Per-op speed.** Irrelevant: the hot loops live in numpy and polars, which
  are C and Rust underneath. The vectorized path beats naive loops in any
  language.

The marginal cost of the second language is small because it rides on the
siloing decision, which was already made for other reasons.

### Why the lake is not in Postgres

One underlying's option chain snapshotted every five minutes is roughly a
thousand contracts times seventy-eight snapshots — about 78k rows per day, for
*one* symbol. Fifty underlyings over a year clears a billion rows.

In CNPG that is WAL volume, backup duration, and restore time: a trading
workload quietly degrading the household's recoverability, which is the one
thing [`purpose.md`](../purpose.md) says Aerie exists to guarantee. Parquet on a
PVC keeps it out of the backup path entirely, DuckDB scans columnar files far
faster than Postgres serves this access pattern, and Parquet passes the test in
`purpose.md`: *if every tool here disappeared tomorrow, what is still readable?*

The trade the lake accepts: it is **not** in the CNPG backup, so its durability
story is its own — a restic target or an object-store sync, sized when it is big
enough to matter. Raw market data is also the one dataset here that is, in
principle, re-collectable from the source. Option chains are the exception, and
that exception is what makes their backup worth doing before the lake is large.

### Why Schwab, given it costs an account

Checked 2026-09-02, because the earlier version of this row claimed Schwab ran
against an account the owner already had. It does not, and the two brokerages
the owner *does* hold accounts at cannot do this at all:

- **Fidelity** publishes no retail API. The only programmatic paths are
  aggregators like SnapTrade — read-only balances and positions, no order
  placement, no market data — or an unofficial PyPI package that drives a
  headless browser. Neither is a foundation for a service with an SLA.
- **Robinhood**'s only official developer product is the **Crypto** Trading API.
  Equities and options have no public API, the terms of service prohibit
  automated access, and every library in the wild calls reverse-engineered
  private endpoints with account suspension as the stated risk. That is a worse
  trade than opening a new account: it wagers an account holding real money.
- **Schwab** requires a Schwab retail self-directed brokerage account, because
  the OAuth flow asks which of the owner's accounts the app may reach. So it is
  a new account plus a developer-app approval, both measured in business days.

The account-free alternative was priced and rejected rather than missed. Market
data and order execution are separate roles, and only data is on the wall clock;
vendors sell the data half with no brokerage relationship at all —
`marketdata.app` bills a **full option chain as one credit** ($12/mo annual for
10k credits a day and 15-minute-delayed options, $30/mo for real-time), and
Massive (formerly Polygon) sells unlimited calls on delayed options at $29/mo.
Either would have the collector running this week for the price of a lunch.

Schwab wins anyway on the strength of being **one credential for both roles**:
free real-time data now and, at Phase 8, execution against the same
authenticated session rather than a second vendor, a second integration and a
second bill. The cost is accepted knowingly — an account opening, an approval
wait, and the seven-day refresh token that Phase 2 exists to absorb.

**If approval stalls or is denied**, the fallback is already priced: implement
the market-data interface against `marketdata.app` instead, and let the broker
half stay unimplemented until Phase 8. The interface is what makes that a
configuration change rather than a rewrite, which is the whole reason it is an
interface.

### Why collection outranks the engine

Equity bars are backfillable from free sources at any time. **Option chains are
not** — historical options data is expensive when it is for sale at all. The
collector is the only component in this plan with a wall-clock dependency: its
value is a function of how long it has been running, not of how good it is.

So it ships before the engine that consumes it, and Phases 0–3 exist only to
make it possible. Scope added to those phases is paid for in months of missing
history.

### One engine, two clocks

`Clock` is an interface. `ReplayClock` walks historical timestamps as fast as
the CPU allows; `LiveClock` ticks on wall time as quotes arrive. A strategy sees
the same context either way — `ctx.now`, `ctx.bars()`, `ctx.chain()`,
`ctx.portfolio` — and emits orders into a `Broker` interface, of which
`SimBroker` is the first implementation.

This is the decision that makes "what is doing best right now" answerable with
the same code that produced the backtest, and it is far cheaper to design in
than to retrofit.

The instrument model is polymorphic from day one: an instrument is an equity
**or** an option contract (underlying, expiry, strike, right), and a position is
a set of legs. Retrofitting multi-leg positions onto a single-symbol engine is a
rewrite, and the owner named all three option-strategy families as targets.

### Overfitting is the threat model

"Evaluate lots of strategies and lots of parameters" is also a precise recipe
for manufacturing false winners. Sweep ten thousand parameter sets and something
posts a beautiful Sharpe on luck alone — this is not a risk to manage later, it
is the *default outcome* of the system as described.

So the honesty layer is a phase, not a footnote (Phase 6):

- Every run declares its in-sample and out-of-sample windows, in the record.
- Every run knows **how many siblings it was selected from**, so a headline
  number can be discounted for selection.
- A buy-and-hold baseline over the identical window sits next to every result.
- An in-sample-only result is never displayed as a headline metric.

A system that skips this does not produce bad answers. It produces confident
ones, which is worse.

### The platform contract

The ask asks how to tell Aerie infra what topology the trading app needs. It
already has an answer: **the app ships a Flux kustomization.**
`deploy/cluster/apps/trading/` declares a CNPG `Cluster`, a PVC, its
deployments, and its ingress route. Aerie's side of the contract is *"hand me a
kustomization and you get Postgres-as-an-operator, backups, ingress behind the
auth wall, log shipping, metrics, and a deploy pipeline."*

That is the same contract a stranger's sideloaded service would use, which makes
building it here an investment in the productization story rather than a
detour from it.

**The extraction seam**, for when trading becomes its own repo: the silo is
`src/Aerie.Trading/` plus `deploy/cluster/apps/trading/` plus the SPA. The SPA
is the only piece with a shared dependency (`@aerie/ui`), and it leaves by
taking a versioned copy of that package as an ordinary npm dependency. Nothing
else crosses the line, and CI should keep it that way — see Phase 1's import
guard.

## Phases

### [ ] Phase 0 — Start the clocks

**Ships:** no behavior. Two waiting periods begin — a brokerage account opening
and a developer-app approval, possibly serial — and the repo gains a Python
toolchain. This phase is first because those long poles are measured in calendar
days and are not shortened by anything else in the plan.

It splits along the only line worth drawing this early: **0a is what a person
has to do at somebody else's website, and 0b is everything a machine can do
here.** They run in parallel and neither gates the other. 0a goes first in
wall-clock terms because it starts a clock that runs whether or not anyone is
watching it; 0b is where the commits actually come from, and it should be done
on the same day rather than while waiting. Keeping the manual list separate also
keeps it honest — anything that turns out to be scriptable belongs in 0b, per
[ethos.md](../ethos.md).

#### [ ] Phase 0a — The part only a person can do

**Ships:** a Schwab brokerage account, a submitted developer application and a
callback URL it will accept, plus a set of facts in this document that are
verified rather than reported. Every item needs a human at a vendor's portal
agreeing to terms on the owner's behalf. That is the entire membership rule for
this list, and it is why the list is as short as it is.

- [x] **Open a Schwab retail self-directed brokerage account.** The Trader API
      authenticates against one — its OAuth consent screen asks which of the
      owner's accounts the app may reach — so no account means no API, whatever
      the developer portal says. Funding it is not required to hold it; whether
      an *unfunded* account satisfies the OAuth account-selection step is the
      first thing to find out, because it decides whether money has to move
      before Phase 2 can work.
- [x] **Find out whether the two waits are serial or parallel** — specifically,
      whether the developer app can be submitted, and approved, while the
      brokerage account application is still pending. Serial is roughly two
      weeks and parallel is roughly one, and the answer changes nothing about
      what gets built, only when Phase 3 can start collecting. Try submitting
      the app the same day the account application goes in; the cost of being
      wrong is a resubmission.
- [x] **Register the Schwab developer app** at the Schwab developer portal.
      Request **both** products — Market Data Production and Accounts and
      Trading Production. Approval is measured in business days and is the
      gating item for Phases 2 and 3.
- [ ] Register the callback URL as `https://trading.${DOMAIN}/api/trading/auth/schwab/callback`,
      supplied from the existing `DOMAIN` variable and never hardcoded.
      **Verify Schwab's current callback rules first** — HTTPS is required, and
      whether a non-loopback host is accepted needs confirming against their
      current documentation rather than assumed.
- [ ] **Verify, and record in this document, the facts Phase 2 and 3 depend on.**
      All of these are widely reported but none should be built against
      unverified:
      - Access-token lifetime (reported: 30 minutes) and refresh-token lifetime
        (reported: 7 days, human re-auth required, not programmatically
        renewable). The 7-day figure is the single most load-bearing unknown in
        the plan — Phase 2's whole shape depends on it.
      - Request rate limit (reported: ~120/minute per app).
      - Minute-bar history depth available from `pricehistory`, and daily-bar
        depth.
      - Whether `/chains` returns greeks and implied volatility inline
        (reported: yes) — this decides whether Phase 9 computes them or merely
        validates them.
- [ ] **Seed the client id and secret** into the parameter store under the two
      paths 0b already added, per
      [secrets-architecture.md](../secrets-architecture.md) — never in the repo,
      not even encrypted, per [ethos.md](../ethos.md). This is the last item in
      the phase because the values do not exist until approval lands, and it is
      a paste rather than a decision because 0b settled the names first.
- [ ] **Gate:** the brokerage account is open · the app is submitted with both
      products requested · the callback URL is registered and its rules
      confirmed against current documentation · the verified facts above are
      written into this document, replacing the reported ones.
- [ ] **Commit:** "Trading: a clock that started, and what Schwab actually says"

#### [~] Phase 0b — The part that does not wait

**Ships:** a Python package with nothing in it, a Makefile target, a CI lane of
its own, and two named-but-unseeded secrets. Nothing here touches Schwab, so
nothing here waits on Schwab.

- [x] Create `src/Aerie.Trading/` — `pyproject.toml` managed by **uv** (lockfile
      committed), `ruff` for lint and format, `pyright` in **strict** mode,
      `pytest`. Python 3.12+.
- [x] Add a `trading-test` target to the [Makefile](../../Makefile) —
      `uv sync --frozen`, `ruff check`, `pyright`, `pytest` — and a CI lane in
      [ci.yml](../../.github/workflows/ci.yml). The lane is **separate** from
      the .NET and web lanes, not bolted into them; that separation is the
      extraction seam expressing itself in CI.
- [x] **Name the Schwab credentials before they exist** —
      `trading/schwab-client-id` and `trading/schwab-client-secret` in
      [`parameters.json`](../../scripts/secrets/parameters.json), the one file
      both halves of the secret path read. `required: false` for now, so a seed
      run before approval skips them with a note instead of failing preflight,
      and no `kubernetes` block yet, so the generator writes no `ExternalSecret`
      for a parameter nothing reads. Phase 2 flips both when it becomes the
      thing that reads them.
- [ ] **Gate:** `make trading-test` green against an empty package · the new CI
      lane runs and passes on a pull request, with the .NET and web lanes
      unaffected by its presence · `New-ExternalSecrets.ps1` still agrees with
      `parameters.json`, which is the check `ci.yml` already runs.
- [ ] **Commit:** "Trading: a silo, a toolchain, and a lane of its own"

### [ ] Phase 1 — The Ledger and the silo's floor

**Ships:** a FastAPI service reachable at `trading.${DOMAIN}`, with its own
Postgres and nothing to say yet. Deliberately boring: this phase is judged on
whether the deploy pipeline, logs, metrics and ingress all work before there is
any logic to confuse them with.

- [ ] `deploy/cluster/apps/trading/` — a Flux kustomization declaring a CNPG
      `Cluster` (its own database, its own credentials, its own
      `ScheduledBackup`), a `Deployment`, a `Service`, and an `IngressRoute`
      behind the existing auth middleware.
- [ ] `control/` — FastAPI + uvicorn. `/healthz` (liveness), `/readyz`
      (database reachable), `/metrics` (Prometheus), and `/api/trading/version`
      reporting the `aerie-revision` it was built from, matching
      [version.md](version.md).
- [ ] Structured JSON logging to stdout, with the field names Fluent Bit's
      pipeline already expects — check
      [`deploy/cluster/observability/controllers/fluent-bit/`](../../deploy/cluster/observability/controllers/fluent-bit/)
      rather than inventing a schema and discovering the mismatch in OpenSearch.
- [ ] SQLAlchemy 2.0 with **Alembic** migrations, run as an init container or a
      Flux `Job` on deploy — the same shape as the existing `migrate-job`, not a
      migrate-on-startup race between replicas.
- [ ] First tables: `instrument`, `data_source`, `ingest_run`. Nothing about
      strategies yet.
- [ ] `Dockerfile.trading` — multi-stage, `uv sync --frozen --no-dev` into a
      slim runtime, non-root, revision stamped as a build arg.
- [ ] **The import guard:** a CI check asserting that nothing under
      `src/Aerie.Trading/` imports from `src/Aerie.Api/` and that no `.csproj`
      references the trading directory. The boundary is a rule that a tired
      afternoon will otherwise erode; make it fail the build.
- [ ] **Gate:** the pod is running in-cluster · logs appear in OpenSearch ·
      the metrics endpoint is scraped · a CNPG backup of the trading database
      has completed once · `/api/trading/version` matches the deployed commit.
- [ ] **Commit:** "Trading: a silo with a floor"

### [ ] Phase 2 — Schwab, and the seven-day problem

**Ships:** an authenticated connection to Schwab that survives token expiry
gracefully and tells the operator when it needs a human. No data collection
yet — this phase is entirely about the credential, because the credential is
what makes Phase 3 possible and what will break Phase 3 in production.

The 7-day refresh token is a **design constraint, not a defect to engineer
around.** Schwab requires periodic human re-authentication on purpose. A
collector that treats it as an error crash-loops weekly; one that treats it as a
scheduled event asks for thirty seconds of attention and keeps its history
intact.

- [ ] `providers/base.py` — a `MarketDataProvider` protocol: `quotes()`,
      `bars()`, `chain()`, `market_hours()`. Schwab is one implementation.
      Per [ethos.md](../ethos.md), a second operator with a different broker
      writes a class, not a fork.
- [ ] `providers/schwab/` — the OAuth 2.0 three-legged flow, with token storage
      in the secret store rather than a file in the pod.
- [ ] An operator-only re-auth page: one button that begins the flow, and a
      callback route that completes it. It sits behind the existing auth wall,
      so only an authenticated operator can complete an authorization — which
      is the correct security property and costs nothing to get.
- [ ] Automatic access-token refresh (short-lived, silent). **Refresh-token
      expiry is surfaced, not retried**: a `token_expires_at` gauge on
      `/metrics`, a Grafana alert at T-24h, and a health endpoint that reports
      degraded rather than dead.
- [ ] Rate limiting client-side, below the verified ceiling, with backoff and
      jitter. One shared limiter for the whole silo — two collectors racing to
      the same quota is a Phase 3 outage.
- [ ] Record every provider call in `ingest_run`: what was asked, what came
      back, how long it took, what it cost against the quota.
- [ ] **Gate:** a chain and a bar series are fetched from production Schwab and
      logged · the access token refreshes across a 30-minute boundary without
      intervention · the T-24h alert fires against a synthetic expiry · a
      deliberately expired refresh token produces a degraded readiness state
      and an alert, not a crash loop.
- [ ] **Commit:** "Trading: a credential that expects to expire"

### [ ] Phase 3 — The lake, and the collectors that fill it

**Ships:** the thing whose value compounds. From the day this merges, option
chain history accumulates that cannot be bought back later. Everything before
this phase existed to make it possible; everything after it is improved by
having started it early.

- [ ] A PVC on the default storage class, modest to start. The **lake root is a
      config parameter**, so relocating it later is a value change and not a
      code change.
- [ ] Lake layout, written down before it has data in it, because rewriting a
      partition scheme is a migration:
      - `bars/{interval}/{symbol}/{year}/{month}.parquet` — OHLCV, UTC
        timestamps, adjusted and unadjusted close both stored.
      - `chains/{underlying}/{date}/{hhmm}.parquet` — one row per contract per
        snapshot: strike, expiry, right, bid, ask, last, volume, open interest,
        and the greeks/IV as the provider reported them.
      - Every file carries the provider, the collection timestamp, and the
        `aerie-revision` of the collector that wrote it.
- [ ] `lake.py` — a DuckDB reader with one job: turn a symbol and a time range
      into a polars frame, hiding the partition layout from every caller. The
      engine must never learn the directory structure.
- [ ] Idempotent writes. A re-run over the same window overwrites its own
      partition and does not append duplicates. Assume the collector will be
      re-run; make that boring.
- [ ] **Market calendar** via `exchange_calendars`, consulted before every
      collection. Do not collect through a holiday and record silence as data.
- [ ] `collect/bars.py` — daily and minute bars: a backfill mode for the depth
      Schwab offers, and an incremental mode after each close.
- [ ] `collect/chains.py` — **the flagship.** Snapshot the full chain for a
      configured watchlist on an interval through the session. Start with a
      small watchlist and a conservative interval; both are configuration, and
      widening them later costs nothing while starting late costs everything.
- [ ] Scheduling: a `CronJob` per collector, or one scheduler process — chosen
      on which is easier to reason about when a collection is missed, not on
      elegance. The missed-collection case is the one that matters.
- [ ] Collection health is visible: rows written, gaps detected, last successful
      run per collector, all on `/metrics`, with an alert for a session that
      collected nothing.
- [ ] A backup path for `chains/` specifically — the one dataset here that
      cannot be re-collected. Sized and scheduled now, while it is small.
- [ ] **Gate:** a full trading session collected end to end with no gaps · a
      DuckDB query returns a chain snapshot as a polars frame in reasonable time
      · a deliberately killed mid-collection run leaves no duplicate rows on
      re-run · lake size per session measured and extrapolated, so the PVC's
      lifetime is a number rather than a hope.
- [ ] **Commit:** "Trading: history starts accumulating"

### [ ] Phase 4 — The engine, proven on equities

**Ships:** a backtest of one boring strategy over collected equity data,
producing a result a person can check by hand. Options are not in this phase,
but the model they need is.

- [ ] `engine/clock.py` — the `Clock` protocol and `ReplayClock`. `LiveClock` is
      Phase 8 and must require no change here when it arrives.
- [ ] `engine/instruments.py` — `Equity` and `OptionContract` (underlying,
      expiry, strike, right, multiplier) behind one `Instrument` type.
      **Written in this phase even though only `Equity` is exercised**, because
      this is the retrofit the plan exists to avoid.
- [ ] `engine/portfolio.py` — `Position` as a set of **legs**, cash, mark-to-
      market, realized and unrealized P&L. Single-leg equity positions are the
      degenerate case, not the model.
- [ ] `engine/broker.py` — the `Broker` protocol and `SimBroker`: fills at the
      next bar's open by default, a configurable slippage model, and a
      commission model. **No same-bar fills on the signal bar** — that single
      shortcut is the most common source of backtests that cannot be
      reproduced live.
- [ ] `engine/strategy.py` — the `Strategy` protocol: a pydantic
      `Params` model declaring each tunable with its type, range and default,
      plus `on_bar(ctx)`. The declared ranges are what Phase 5 sweeps.
- [ ] `strategies/` — one deliberately boring reference strategy (a moving
      average crossover). It is a test fixture for the engine, not a candidate.
- [ ] **Determinism:** the same inputs and seed produce byte-identical results.
      Asserted in a test, because a non-reproducible backtest cannot be
      debugged and a comparison between two of them means nothing.
- [ ] A hand-checkable fixture: a tiny synthetic price series with known correct
      P&L, asserted to the cent. Every later engine change is measured against
      it.
- [ ] **Gate:** the reference strategy backtests over collected data · the
      hand-checked fixture passes to the cent · two runs of identical inputs are
      identical · a lookahead test fails the build if a strategy can see a bar
      it should not.
- [ ] **Commit:** "Trading: an engine, and a fixture that proves it"

### [ ] Phase 5 — Runs, sweeps, and the queue

**Ships:** launching a thousand parameter combinations and watching them land.
This is the ask's "lots of strategies, lots of parameters" made operational.

- [ ] Tables: `strategy`, `param_set`, `run`, `trade`, `run_metric`. A `run`
      records its strategy, its parameters, its data window, the
      `aerie-revision` that produced it, and the lake state it read.
- [ ] A **Postgres work queue** — `SELECT … FOR UPDATE SKIP LOCKED`, retry
      counts, visibility timeouts. No Redis, no Celery, no new infrastructure,
      and queue depth becomes rows the control panel already reads.
- [ ] A worker deployment, horizontally scalable, with a `PodDisruptionBudget`
      and a CPU limit — a sweep is deliberately unbounded compute, and the
      cluster runs a household. The limit is the point.
- [ ] A sweep: pick a strategy, pick ranges over its declared parameters, get
      the cross product, enqueue N runs, watch them complete. Sweep size is
      estimated and confirmed **before** enqueueing.
- [ ] Metrics per run: total and annualized return, Sharpe, Sortino, max
      drawdown and its duration, exposure, turnover, win rate, trade count. All
      computed in one place; a metric defined twice will diverge.
- [ ] Cancellation, and resumption after a worker dies mid-run.
- [ ] **Gate:** a 1,000-run sweep completes · killing a worker mid-sweep loses
      no runs and duplicates none · metrics for a hand-checked run match a
      hand-computed answer · the cluster stays responsive under a full sweep,
      measured rather than assumed.
- [ ] **Commit:** "Trading: sweeps, and a queue that survives a lost worker"

### [ ] Phase 6 — The honesty layer

**Ships:** results that can be trusted, or at least whose untrustworthiness is
visible. This phase adds no capability and is the most valuable one in the plan.

By this point Phase 5 can produce ten thousand results, some of which will look
excellent for no reason at all. Everything here exists to keep that from being
mistaken for a finding.

- [ ] Every run declares **in-sample and out-of-sample windows** in its record.
      A run with no out-of-sample window is a valid object that can never be a
      headline number.
- [ ] **Walk-forward:** rolling train/test windows, parameters chosen on each
      train window, results stitched from the test windows only. This is the
      number the leaderboard sorts on.
- [ ] **Selection accounting:** a run knows how many siblings its sweep
      produced. Report an adjusted figure alongside the raw Sharpe — a
      deflated Sharpe or an equivalent multiple-testing correction — so
      "best of 10,000" is visibly different from "best of 3."
- [ ] **Baselines**, computed over the identical window and shown next to every
      result: buy-and-hold on the underlying, and buy-and-hold on a broad index.
      A strategy that loses to buying the index has told you something, and it
      should not take arithmetic to notice.
- [ ] Transaction-cost sensitivity: every result re-scored at a higher cost
      assumption. A strategy that only works at zero slippage is identified as
      such automatically.
- [ ] The API **refuses** to serve an in-sample-only figure in a field labeled
      as performance. The guard is in the serializer, not in a convention, and
      not in the UI.
- [ ] **Gate:** a deliberately overfit strategy — parameters fit to noise —
      ranks poorly on the leaderboard, and its in-sample and walk-forward
      numbers visibly diverge. If it ranks well, this phase is not done.
- [ ] **Commit:** "Trading: results that admit what they are"

### [ ] Phase 7 — The control panel

**Ships:** the ask's top-level interaction. Strategies as the primary object,
sweeps launchable from the UI, and one leaderboard.

- [ ] `src/Aerie.Web/apps/trading/` — a Vite React app in the existing
      workspace, consuming `@aerie/ui` for tokens, day/night, and the shared top
      bar. It must add no palette of its own; the design system is the entire
      mechanism keeping the suite looking like one product.
- [ ] Add the app to the workspace build list in the
      [Makefile](../../Makefile), CI matrix, and its MSBuild `Inputs` glob —
      the per-app plumbing that [design-system-mvp.md](design-system-mvp.md)
      Phase 1 documented as the one thing that does not generalize for free.
      **Note the seam:** this SPA is served by the trading service, not by
      `Aerie.Api`, so it does not join `wwwroot/apps/`.
- [ ] **Strategies list** — every strategy, its parameter space, its best
      walk-forward result, its live status. The top-level interaction from the
      ask.
- [ ] **Strategy detail** — parameter ranges, a sweep launcher with an estimated
      run count before you commit, and the runs that came from it.
- [ ] **Leaderboard** — sortable across everything, sliceable by today / this
      week / since inception, and by backtest vs. live. The honesty columns from
      Phase 6 are not optional columns; they ship visible by default.
- [ ] **Run detail** — trades, the equity curve, the metrics, and the exact
      parameters and revision, so a result can be reproduced.
- [ ] Grafana carries deep-dive time series. Do not build a charting stack to
      compete with a tool already deployed and already good at this.
- [ ] **Gate:** launch a sweep from the UI and watch it complete · the
      leaderboard answers "what did best today" in one glance, which is the
      ask's own success criterion · the app is correct in day and night mode ·
      `make test-web` green.
- [ ] **Commit:** "Trading: a control panel"

### [ ] Phase 8 — The live clock

**Ships:** paper trading. Strategies run forward against live quotes, and the
leaderboard gains a column that changes during the day.

- [ ] `LiveClock` — ticking on wall time as quotes arrive. If this requires any
      change to `Strategy`, Phase 4 got the abstraction wrong and the fix
      belongs there rather than in a branch here.
- [ ] `live_session` — a promoted parameter set, its start time, its starting
      capital, its current state. Promotion is an insert.
- [ ] A live loop process: subscribe to quotes for the instruments its sessions
      need, tick the clock, route orders to `SimBroker` against live prices.
- [ ] Mark-to-market on a schedule, persisted, so intraday P&L survives a
      restart.
- [ ] Reconciliation: a live session and a backtest over the same window should
      agree within a stated tolerance. **Where they disagree, the backtest is
      wrong**, and the difference is the fidelity debt made visible.
- [ ] Restart safety — a live session survives a pod restart without
      double-counting a fill or losing a position.
- [ ] **Gate:** a session runs a full trading day unattended · P&L survives a
      deliberate pod kill · the backtest/live reconciliation is within tolerance
      or the gap is explained in this document.
- [ ] **Commit:** "Trading: the same engine, on a live clock"

### [ ] Phase 9 — Options

**Ships:** the actual target. Chain-aware strategies, multi-leg positions, and
the three strategy families the owner named.

This is the largest phase and should be split into its own directory under
[`docs/plans/`](README.md) when it starts, per the lifecycle in the plans
README. What follows is its shape, not its detail.

- [ ] Chain-aware context: `ctx.chain(underlying, expiry)` reading snapshots
      from the lake, with the selection helpers strategies actually need —
      by delta, by moneyness, by days to expiry.
- [ ] Multi-leg orders: verticals, calendars, straddles, condors, as one
      atomic order with one fill decision.
- [ ] **Expiry, exercise and assignment.** Expiry worthless, expiry in the
      money, early assignment on short options, pin risk. This is where
      premium-selling backtests most often lie about returns, and it is
      unavoidable given premium selling is a named target.
- [ ] Execution modeling worth more than it is for equities: option spreads are
      wide, and mid-price fills are a fiction. Fill at a configurable point
      between mid and the far touch, and make the assumption visible in every
      result.
- [ ] Greeks: use the provider's when present, and **validate them against an
      independent implementation** (QuantLib or py_vollib) rather than trusting
      either alone.
- [ ] A margin approximation for premium selling. Cash-secured and defined-risk
      positions first; anything needing portfolio margin is out of scope until
      it is not.
- [ ] Volatility surface fitting for the vol family — IV rank, term structure,
      skew. Depends on chain history depth, which is why Phase 3 shipped first.
- [ ] **Gate:** a covered call and a vertical spread backtest, hand-checked
      through expiry including an assignment · greeks agree with the independent
      implementation within tolerance · the walk-forward and baseline machinery
      from Phase 6 applies to option results unchanged.
- [ ] **Commit:** "Trading: options"

## Deferred, deliberately

- **Real money.** Out of scope for this plan, by the owner's instruction. When
  it arrives it is its own plan and its own security review, and at minimum it
  needs: order routing to a live broker, a kill switch, position reconciliation
  against the broker's own record, a hard loss limit enforced outside the
  strategy, and an audit trail. The current design's one concession to that
  future is that `Broker` is an interface. **Nothing else in this plan should
  be justified by it.**
- **Crypto and FX.** The instrument model does not foreclose them; nothing here
  builds them.
- **Its own repo.** The seam is maintained from Phase 1 and enforced in CI. The
  move happens when there is a reason, not on a schedule.
- **Python strategies from published research.** The `Strategy` protocol is the
  contract; adapting an outside implementation to it is per-strategy work, not
  platform work. Revisit if it happens three times.

## Verification

The plan is done when these are all true, in this order:

1. Option chain history has been accumulating for long enough to backtest
   against — the only item on this list that cannot be accelerated by working
   harder, which is why Phase 3 is where it is.
2. A strategy can be added by writing one file and deploying, exactly as the ask
   assumes.
3. A sweep over that strategy's declared parameters runs without the cluster
   noticing.
4. The leaderboard answers "what is doing best right now" in one glance, across
   backtests and live paper sessions together.
5. A deliberately overfit strategy is visibly ranked as such.
6. Nothing under `src/Aerie.Trading/` imports from `Aerie.Api`, and CI proves it
   on every commit.
