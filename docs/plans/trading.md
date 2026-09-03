# Trading — a strategy laboratory that rides Aerie as a platform

**Status:** Phases 0b and 1 built on 2026-09-02; Phase 0a is waiting on Schwab.
What is left of Phase 1 is its gate — five checks that need the cluster — plus
one line an operator adds to the site repository, both written out under that
phase.

**The plan was re-cut on 2026-09-02 to build on a synthetic data source first.**
Schwab was the second phase and is now the eighth. Every phase between them is
built and deployed against a source that needs no credential, no approval and no
market hours, which turns the Schwab integration from a dependency the whole
plan waits behind into one provider class dropped into machinery already proven.
See [Why the first data source is synthetic](#why-the-first-data-source-is-synthetic)
for why this *accelerates* option-chain collection rather than delaying it, and
Phase 8 for the standing offer to promote it the day approval lands.

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
| Where does data come from? | **Schwab Trader API** — free real-time quotes and chains, from the same credential that eventually places orders. It requires a **Schwab retail brokerage account, which the owner does not have**; opening one is the first item of Phase 0a. Chosen over the account-free data vendors knowingly — see [Why Schwab, given it costs an account](#why-schwab-given-it-costs-an-account) |
| What fills the lake *first*? | **A synthetic source** — seeded pseudo-random bars and structurally-valid noise chains, no credential and no network. It ships in Phase 2 and everything through Phase 7 is built on it. Schwab is the *second* implementation of the same interface, in Phase 8. See [Why the first data source is synthetic](#why-the-first-data-source-is-synthetic) |
| How does a data source plug in? | **`MarketDataProvider` is an interface**, per [ethos.md](../ethos.md), and it gets two implementations before it hardens. A second operator with a different broker writes a class, not a fork |
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
| Alerting | Grafana + Uptime Kuma | Carries the Schwab token-expiry alarm — see Phase 8 |
| Design system | `@aerie/ui`, [`src/Aerie.Web/packages/ui/`](../../src/Aerie.Web/packages/ui/) | The control panel looks like Aerie for free |
| Commit binding | `aerie-revision`, [`version.md`](version.md) | Every run record stores the revision that produced it |

What trading explicitly does **not** consume: `Aerie.Api`'s process, its
`AerieContext`, its Quartz scheduler, its module system, or any of its C#. Those
are the couplings that would make extraction a rewrite.

## Design commitments

The decisions that are cheap now and expensive later. Everything in the phases
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
free real-time data at Phase 8 and, if real money ever arrives, execution
against the same authenticated session rather than a second vendor, a second
integration and a second bill. The cost is accepted knowingly — an account
opening, an approval wait, and the seven-day refresh token that Phase 8 exists
to absorb.

**If approval stalls or is denied**, the fallback is already priced: implement
the market-data interface against `marketdata.app` instead, and let the broker
half stay unimplemented indefinitely. The interface is what makes that a
configuration change rather than a rewrite, which is the whole reason it is an
interface — and re-cutting the plan onto a synthetic source makes the stall
cheap in the first place, since nothing before Phase 8 is waiting on the
answer.

### Why collection outranks the engine

Equity bars are backfillable from free sources at any time. **Option chains are
not** — historical options data is expensive when it is for sale at all. The
collector is the only component in this plan with a wall-clock dependency: its
value is a function of how long it has been running, not of how good it is.

That fact set the original phase order, and it still sets the priority. What
changed is the reading of what "ships the collector sooner" means in practice —
see the next section.

### Why the first data source is synthetic

The first version of this plan put Schwab second, so that collection could start
the moment approval landed. Working through it exposed the flaw: **approval
lands on a repository that has none of the machinery collection needs.** The
partition layout, the DuckDB reader, idempotent writes, calendar handling,
scheduling and the health metrics were all *downstream* of the credential, so
the clock would have kept running while they were written, debugged and
deployed.

Inverting it is strictly better on the metric that motivated the original order.
Phase 3 builds and hardens all of that against a source that is available right
now, and Phase 8 becomes a provider class dropped into a collector that already
works. Real chains start accumulating within hours of approval instead of within
weeks of it. **The synthetic source is not a delay to the wall clock; it is what
gets the wall clock the shortest possible path.**

Three further things fall out of it, none of which were the reason but all of
which are worth having:

- **The whole stack becomes testable in CI.** No network, no credentials, no
  waiting for 9:30am. A collector run, a backtest and a sweep all execute in
  seconds against generated data. The original plan could not test a collector
  end to end without a live Schwab session.
- **It is the null hypothesis.** The generator has **zero alpha by
  construction** — a random walk has no exploitable structure. So any strategy
  posting an attractive Sharpe against it is *provably* overfit, and Phase 6's
  gate stops being a judgment call and starts being arithmetic. This is the one
  place where a deliberately unrealistic source beats a realistic one.
- **The interface gets two implementations before it hardens.** A protocol
  written alongside Schwab alone would quietly encode Schwab's quirks as though
  they were the shape of market data. Written against the trivial
  implementation first and the real one second, it has to be honest.

**What this deliberately gives up**, and the reason it is recorded here rather
than discovered later: synthetic data is clean, and real market data is not.
Gaps, halts, splits and corporate actions, bad prints, zero-bid contracts, stale
quotes and DST seams are all pathologies the generator does not model, so none
of them are exercised until Phase 8. Fault injection — a knob that makes the
generator emit those deliberately — was considered and **deferred by the owner
to Phase 8**, on the reasoning that real data should teach which faults actually
occur rather than guessing at them. That decision stands; it is carried as a
named, budgeted risk in Phase 8 rather than as an assumption that first contact
will go smoothly.

**Scope discipline still applies, and now applies to the synthetic source too.**
It exists to unblock the phases after it, not to be interesting. It is pure
noise on purpose, it never becomes a simulator, and the moment it starts
acquiring realism features it has stopped paying for itself.

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
`deploy/cluster/trading/` declares a CNPG `Cluster`, a PVC, its deployments,
and its ingress route. Aerie's side of the contract is *"hand me a
kustomization and you get Postgres-as-an-operator, backups, ingress behind the
auth wall, log shipping, metrics, and a deploy pipeline."*

That is the same contract a stranger's sideloaded service would use, which makes
building it here an investment in the productization story rather than a
detour from it.

**The extraction seam**, for when trading becomes its own repo: the silo is
`src/Aerie.Trading/` plus `deploy/cluster/trading/` plus the SPA. The SPA
is the only piece with a shared dependency (`@aerie/ui`), and it leaves by
taking a versioned copy of that package as an ordinary npm dependency. Nothing
else crosses the line, and CI keeps it that way — see Phase 1's import guard.

Two objects sit outside that boundary and are worth naming rather than
discovering during the extraction, because both are what the platform charges
for the services it provides: a `ServiceMonitor` in
[`observability/config/scrape/trading.yaml`](../../deploy/cluster/observability/config/scrape/trading.yaml)
(every instance of a `monitoring.coreos.com` CRD in this repository lives in
that layer, for the cold-rebuild ordering reason its own kustomization
explains), and one namespace added to the selector in that directory's
`cloudnative-pg.yaml`. Extraction deletes both. Everything else the silo needs
from the platform — the pull secret, the WAL credential, the namespace — is a
target block in `parameters.json` or a line in `namespaces.yaml`, which is the
same shape any sideloaded service would add.

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

**What the re-cut changed here:** starting these clocks early is still worth
doing, and 0a is still first for that reason. What it no longer is, is
load-bearing. Under the original order, everything from Phase 2 on was parked
behind this approval; now nothing before Phase 8 waits on it, and 0a's only
consequence is how early Phase 8 becomes available to promote.

#### [ ] Phase 0a — The part only a person can do

**Ships:** a Schwab brokerage account, a submitted developer application and a
callback URL it will accept. Every item needs a human at a vendor's portal
agreeing to terms on the owner's behalf. That is the entire membership rule for
this list, and it is why the list is as short as it is.

**Shortened by the 2026-09-02 re-cut.** Verifying Schwab's token lifetimes,
rate limits and chain payload, and seeding the client id and secret, all used to
live here — because Phase 2 was Schwab and needed them immediately. Nothing
reads them until Phase 8 now, and a fact verified today against documentation
that changes before it is built on is a fact verified twice. They moved to
Phase 8, next to the code that depends on them. What is left is only what starts
a clock.

- [x] **Open a Schwab retail self-directed brokerage account.** The Trader API
      authenticates against one — its OAuth consent screen asks which of the
      owner's accounts the app may reach — so no account means no API, whatever
      the developer portal says. Funding it is not required to hold it; whether
      an *unfunded* account satisfies the OAuth account-selection step is the
      first thing to find out, because it decides whether money has to move
      before Phase 8 can work.
- [x] **Find out whether the two waits are serial or parallel** — specifically,
      whether the developer app can be submitted, and approved, while the
      brokerage account application is still pending. Serial is roughly two
      weeks and parallel is roughly one. Under the re-cut this no longer
      changes when anything gets built — Phases 1 through 7 do not wait on it —
      only when Phase 8 can be promoted. Try submitting the app the same day the
      account application goes in; the cost of being wrong is a resubmission.
- [x] **Register the Schwab developer app** at the Schwab developer portal.
      Request **both** products — Market Data Production and Accounts and
      Trading Production. Approval is measured in business days and is the
      gating item for Phase 8, and for Phase 8 alone.
- [ ] Register the callback URL as `https://trading.${DOMAIN}/api/trading/auth/schwab/callback`,
      supplied from the existing `DOMAIN` variable and never hardcoded.
      **Verify Schwab's current callback rules first** — HTTPS is required, and
      whether a non-loopback host is accepted needs confirming against their
      current documentation rather than assumed.
- [ ] **Note the approval date when it lands**, in this document, and tell the
      owner. It is the only signal Phase 8 waits for, and an approval nobody
      noticed is an approval that buys nothing.
- [ ] **Gate:** the brokerage account is open · the app is submitted with both
      products requested · the callback URL is registered and its rules
      confirmed against current documentation.
- [ ] **Commit:** "Trading: a clock that started"

#### [x] Phase 0b — The part that does not wait

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
      for a parameter nothing reads. Phase 8 flips both when it becomes the
      thing that reads them.
- [x] **Gate:** `make trading-test` green against an empty package · the new CI
      lane runs and passes on a pull request, with the .NET and web lanes
      unaffected by its presence · `New-ExternalSecrets.ps1` still agrees with
      `parameters.json`, which is the check `ci.yml` already runs.
- [x] **Commit:** "Trading: a silo, a toolchain, and a lane of its own"

### [x] Phase 1 — The Ledger and the silo's floor

**Ships:** a FastAPI service reachable at `trading.${DOMAIN}`, with its own
Postgres and nothing to say yet. Deliberately boring: this phase is judged on
whether the deploy pipeline, logs, metrics and ingress all work before there is
any logic to confuse them with.

**Built 2026-09-02.** Everything below is committed; the gate's five checks are
the part that needs a cluster, and each is marked with what was verified
locally in its place. Three things came out of building it and are recorded in
place rather than as a footnote: the layer is a sibling of `photos.yaml` rather
than a directory inside `apps/` (below), the wall bounces an un-enrolled
browser to a sign-in shell this host does not serve (below), and the migration
runs as an init container rather than a Flux `Job` because a `Job`'s spec is
immutable and its name cannot carry the image tag.

- [x] `deploy/cluster/trading/` — a Flux kustomization declaring a CNPG
      `Cluster` (its own database, its own credentials, its own
      `ScheduledBackup`), a `Deployment`, a `Service`, and an ingress behind the
      auth middleware.

      **Two deviations from this bullet as written, both deliberate.**

      *The path.* This bullet said `deploy/cluster/apps/trading/`. It is
      [`deploy/cluster/trading/`](../../deploy/cluster/trading/) with its own
      [`trading.yaml`](../../deploy/cluster/trading.yaml), a sibling of
      `photos.yaml`, for the reason that file argues at length about Immich and
      this plan argues one section earlier about the silo: `apps.yaml` carries
      `wait: true` and holds the family's own site tier, so a trading deploy
      that will not converge would hold the house NotReady. It is also the
      shape that can be lifted out — a directory nested inside another layer's
      Kustomization cannot move without editing the layer containing it.

      *`IngressRoute` → `Ingress`.* Every hostname this cluster serves is a
      plain `Ingress` with no `tls:` block, so the wildcard TLSStore default
      terminates it; an `IngressRoute` here would be the one route in the house
      whose certificate story is configured somewhere else. The middleware it
      needs is reachable by annotation either way.

      **And one thing the auth wall does that had to be answered.**
      `AuthController.Verify` rebuilds its redirect's origin from
      `X-Forwarded-Host`, so an un-enrolled browser at `trading.${DOMAIN}` is
      bounced to `/apps/auth/` *on this host* — which serves no sign-in shell,
      so the first thing an operator would see is a 404 from a service that is
      working perfectly. The trading service answers that path with a redirect
      to the real shell, from a configured URL (`TRADING_SIGN_IN_URL`) rather
      than a derived one, so the silo does not learn Aerie's URL structure. The
      cookie is domain-wide, so signing in there covers this host; the return
      trip is one manual navigation until Phase 7 puts a UI here worth
      returning to.
- [x] `control/` — FastAPI + uvicorn. `/healthz` (liveness), `/readyz`
      (database reachable), `/metrics` (Prometheus), and `/api/trading/version`
      reporting the `aerie-revision` it was built from, matching
      [version.md](version.md) — same three field names as
      `GET /api/aerie-revision`, and `Aerie-Revision` on every response
      including the failures.

      The revision is read from a `build.json` written **into the package**
      by `Dockerfile.trading`, not from an environment variable: a revision the
      deployment can supply is one the deployment can get wrong, which is the
      same argument that put the sha in the .NET assembly rather than in the
      pod spec.
- [x] Structured JSON logging to stdout, in `AddJsonConsole`'s shape rather
      than a new one, checked against
      [`fluent-bit/service_tag.lua`](../../deploy/cluster/observability/controllers/fluent-bit/service_tag.lua)
      rather than assumed. Two properties there are load-bearing and are
      asserted in tests: `State.Service` is never set (its presence marks a
      line as *relayed*, and a relayed line is deliberately never attributed to
      its container image), and `State.AerieRevision` is set on every line —
      which makes this the first thing to use the override path that script
      already carried for "an image deployed at a moving tag".
- [x] SQLAlchemy 2.0 with **Alembic** migrations, run as an **init container**.
      The `Job` alternative was priced and rejected: a `Job`'s spec is
      immutable once created, so one whose image tag changes every deploy needs
      either a name carrying that tag — `trading-migrate-<14 digits>-<40 hex>`
      is past the name length limit — or `force: true` on the Kustomization,
      which is a blunt instrument to reach for over one object. With
      `replicas: 1` and `strategy: Recreate`, exactly one process runs
      `alembic upgrade head` at a time, which is the property this bullet is
      asking for. When Phase 5 adds workers, they do not migrate; if this
      Deployment ever needs a second replica, the migration moves to a `Job`
      first.
- [x] First tables: `instrument`, `data_source`, `ingest_run`. Nothing about
      strategies yet. `instrument` is polymorphic from day one — an equity or
      an option contract, with a CHECK constraint asserting that an option has
      all four of its defining fields and an equity has none of them — because
      that is the retrofit this plan exists to avoid.
- [x] `Dockerfile.trading` — multi-stage, `uv sync --frozen --no-dev` into a
      slim runtime, non-root, revision stamped as a build arg. Its build
      context is `src/Aerie.Trading/` rather than the repository root, unlike
      every other image here: Docker refuses a `COPY` that escapes the context,
      so the extraction seam is enforced by the build itself.
- [x] **The import guard:** the `trading-boundary` job in
      [ci.yml](../../.github/workflows/ci.yml). Five checks rather than the two
      this bullet names, because the two as written cannot be made honestly:
      the silo's own comments talk about `Aerie.Api` constantly — the log shape
      it copies, the wall it sits behind — and a guard that cannot tell prose
      from code is a guard someone turns off. Each check matches a *mechanism*
      instead: an import statement, a parent-traversing path, a `.csproj`
      reference, an import of `aerie_trading` from outside, and a `COPY` out of
      the image's context. Each also asserts it found files to look at, because
      a guard that greps a renamed path passes silently forever.
- [ ] **Gate:** the pod is running in-cluster · logs appear in OpenSearch ·
      the metrics endpoint is scraped · a CNPG backup of the trading database
      has completed once · `/api/trading/version` matches the deployed commit.

      Every one of these needs the cluster. What was verified locally in their
      place: the image builds and its container answers all four endpoints,
      with `/readyz` correctly reporting `degraded` against no database and
      `/api/trading/version` returning the sha passed as a build arg; `alembic
      upgrade head --sql` renders the schema from inside the image; a test
      asserts the migration and the models describe the same schema; every
      kustomization under `deploy/` builds, and `New-ExternalSecrets.ps1
      -Check` is in sync.
- [x] **The one step that lives in the site repository.**
      `${TRADING_IMAGE_TAG}` resolves from the `aerie-image-tags` ConfigMap,
      and `ImageUpdateAutomation` only *moves* a value next to an existing
      marker — it cannot add one, so the line had to arrive by hand exactly as
      the api and kiosk-files lines did. Committed 2026-09-02:

      ```yaml
      TRADING_IMAGE_TAG: latest  # {"$imagepolicy": "flux-system:trading:tag"}
      ```

      `latest` as the initial value, matching how those two started: the
      `aerie-trading` image does not exist until the next push to `main` builds
      it, and the `ImagePolicy` replaces this with a real
      `<timestamp>-<sha>` on its first successful scan. Until then the pod is
      `ImagePullBackOff` rather than the whole layer failing its build, which
      is the better of the two failures.
- [x] **Commit:** "Trading: a silo with a floor"

### [ ] Phase 2 — The synthetic market

**Ships:** the `MarketDataProvider` interface and a generator that implements
it, so that every phase after this one has data to work on without a credential,
a network call or a market being open. This is the phase that decouples the rest
of the plan from Schwab.

It is deliberately trivial. The generator is **pure noise, permanently** — it
never grows an implied-volatility surface, a jump model or a regime switch,
because the moment it becomes interesting it stops being a control and starts
being a thing whose own behavior has to be reasoned about. Its job is to have
the right *shape*, not the right *statistics*.

- [ ] `providers/base.py` — the `MarketDataProvider` protocol: `quotes()`,
      `bars()`, `chain()`, `market_hours()`. **Written here rather than
      alongside Schwab on purpose.** A protocol whose only implementation is a
      vendor API ends up encoding that vendor's quirks as though they were the
      shape of market data; this one has to satisfy a second implementation
      before it hardens, and the trivial one is the better first because it can
      be bent to the interface rather than the reverse.
- [ ] `providers/synthetic/` — the generator. **Stateless and deterministic:** a
      bar is derived from a hash of `(seed, symbol, timestamp, interval)` rather
      than from a stored path, so the same request returns the same bar forever,
      no generated series has to be persisted, and a backfill and an incremental
      collection of the same window agree by construction. That last property is
      what Phase 3's idempotency gate is actually testing, and this makes it
      testable without a network.
- [ ] Bars: a random walk per symbol — open, high, low, close, volume, with the
      OHLC relationships internally consistent, because a collector or an engine
      that trips over `high < close` should trip over real data, not over the
      fixture.
- [ ] Chains: **structurally valid, numerically meaningless.** A plausible
      strike ladder and expiry calendar, the full row shape Phase 3's partition
      layout expects, and noise in every price, greek and IV field. This is
      enough to exercise the collector, the partition scheme, the DuckDB reader
      and the health metrics — the plumbing, which is all Phase 3 is about.

      **The guardrail:** every row the lake stores already carries its
      `data_source`, and an option backtest against a synthetic source is
      **refused** — in code, the way Phase 6 refuses to serve an in-sample-only
      figure, not by convention and not in the UI. Noise chains are fine for
      moving bytes and meaningless for pricing anything, and the distance
      between those two is exactly where a plausible-looking wrong answer would
      come from. Phase 10 is unaffected: it lands after Phase 8, so real chain
      history exists by the time anything wants to backtest against one.
- [ ] **The calendar is real even though the prices are not.** The generator
      honors `exchange_calendars` — same session boundaries, same holidays, same
      early closes. A synthetic session that runs 24/7 would leave Phase 3's
      calendar handling completely unexercised, which is the one piece of Phase
      3 that fails silently rather than loudly.
- [ ] Configuration: universe, seed, starting price level, drift and volatility,
      all values rather than code. Drift defaults to zero.
- [ ] **Zero alpha, asserted.** A test establishes that generated returns carry
      no exploitable autocorrelation at the sample sizes the plan uses. Phase 6's
      gate rests on this being true, so it is a test rather than a claim — if the
      generator ever acquires structure, the phase that depends on it should
      break loudly here rather than quietly there.
- [ ] Registered as a `data_source` row, with the seed and configuration
      recorded, so a run is reproducible from its provenance alone.
- [ ] **Gate:** bars and chains are produced for a configured universe · two
      identical requests return byte-identical data · a full simulated session
      generates in CI in seconds with no network · generated returns show no
      exploitable autocorrelation · the calendar refuses to generate a session
      on a market holiday.
- [ ] **Commit:** "Trading: a market that does not exist"

### [ ] Phase 3 — The lake, and the collectors that fill it

**Ships:** every piece of machinery that stands between a provider and a
queryable history — the partition layout, the reader, idempotent writes,
calendar handling, scheduling and collection health — built and hardened
against the synthetic source.

This is the phase the re-cut was for. Under the original order it sat *behind*
the credential, so approval-day would have started a scramble to write all of
this while the clock ran. Built here instead, Phase 8 is a provider class
landing in a collector that already works, and real chain history starts
accumulating within hours of approval rather than weeks.

**Every bullet below is provider-agnostic**, and that is the acceptance
criterion for the phase as much as any individual gate: if any of it needs
changing when Schwab arrives, the interface from Phase 2 was drawn in the wrong
place and the fix belongs there.

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
- [ ] `collect/bars.py` — daily and minute bars: a backfill mode for whatever
      depth the configured provider offers, and an incremental mode after each
      close.
- [ ] `collect/chains.py` — **the flagship.** Snapshot the full chain for a
      configured watchlist on an interval through the session. Start with a
      small watchlist and a conservative interval; both are configuration, and
      widening them later costs nothing while starting late costs everything.
      It collects noise today and real chains the day Phase 8 lands, and the
      code does not know the difference — which is the whole point of building
      it now.
- [ ] Scheduling: a `CronJob` per collector, or one scheduler process — chosen
      on which is easier to reason about when a collection is missed, not on
      elegance. The missed-collection case is the one that matters.
- [ ] Collection health is visible: rows written, gaps detected, last successful
      run per collector, all on `/metrics`, with an alert for a session that
      collected nothing.
- [ ] A backup path for `chains/` specifically — the one dataset here that
      cannot be re-collected. **Built now and left running against synthetic
      data**, so that the first chain snapshot worth keeping is already inside a
      backup path that has been exercised, rather than being the run that tests
      it. Synthetic chains are worthless and backing them up is nearly free;
      the point is that the mechanism is proven before the data is precious.
- [ ] **Gate:** a full simulated session collected end to end with no gaps · a
      DuckDB query returns a chain snapshot as a polars frame in reasonable time
      · a deliberately killed mid-collection run leaves no duplicate rows on
      re-run · a collection attempt on a market holiday is refused rather than
      recording silence as data · the whole session collects in CI without a
      network · lake size per session measured and extrapolated **at the row
      counts a real chain implies, not the synthetic watchlist's**, so the PVC's
      lifetime is a number rather than a hope.
- [ ] **Commit:** "Trading: a lake, and the machinery to fill it"

### [ ] Phase 4 — The engine, proven on equities

**Ships:** a backtest of two boring strategies over collected equity data,
producing a result a person can check by hand. Options are not in this phase,
but the model they need is.

The two strategies are **shipped components, not test fixtures.** An earlier
draft called the reference strategy "a test fixture for the engine, not a
candidate"; the owner's requirement is that the vertical arrives in production
demonstrating itself, which means the app boots with strategies registered and
results already in the leaderboard. Phase 7 seeds them. This phase writes them,
and writes them to the standard of something an operator will read first when
authoring their own.

- [ ] `engine/clock.py` — the `Clock` protocol and `ReplayClock`. `LiveClock` is
      Phase 9 and must require no change here when it arrives.
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
- [ ] `strategies/` — **two shipped reference strategies**, which together are
      the proof-of-concept the vertical deploys with:

      *`buy_and_hold`* — buy at the first bar, hold to the last. Ten lines, no
      parameters worth sweeping. It is the smallest honest example of the
      `Strategy` protocol, so it is the file to read before writing one, and
      Phase 6 needs it as a baseline regardless, so it costs nothing.

      *`ma_crossover`* — a moving-average crossover with fast and slow windows
      declared as swept parameters. It exists because it is the only one of the
      two that can exercise a parameter schema, a sweep launcher and a
      multi-run leaderboard. A UI with one unparameterized strategy in it does
      not demonstrate the product.

      Neither is a candidate for making money, and the plan says so in the
      strategy's own description field so that the UI says so too.
- [ ] **What the reference strategies are expected to do, written down before
      they run.** Against a zero-drift random walk, `ma_crossover` should
      **lose to `buy_and_hold` after costs** — it trades, trading costs money,
      and there is no signal to pay for it. That is the theoretically correct
      outcome, and it makes the seeded demo an assertion rather than a
      decoration: if the shipped leaderboard ever shows the crossover beating
      buy-and-hold on synthetic data, the cost model, the fill model or the
      engine is wrong, and it is wrong on the app's own front page. Asserted as
      a test here and visible as a result in Phase 7.
- [ ] **Determinism:** the same inputs and seed produce byte-identical results.
      Asserted in a test, because a non-reproducible backtest cannot be
      debugged and a comparison between two of them means nothing.
- [ ] A hand-checkable fixture: a tiny synthetic price series with known correct
      P&L, asserted to the cent. Every later engine change is measured against
      it.
- [ ] **Gate:** both reference strategies backtest over collected data · the
      hand-checked fixture passes to the cent · two runs of identical inputs are
      identical · a lookahead test fails the build if a strategy can see a bar
      it should not · `ma_crossover` loses to `buy_and_hold` on synthetic data
      after costs, as predicted above.
- [ ] **Commit:** "Trading: an engine, a fixture that proves it, and two strategies to run"

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
- [ ] **A named demo sweep**, defined here and enqueued by Phase 7's seed job:
      `ma_crossover` over a small grid of its two windows, on one synthetic
      symbol, over a fixed window. Small enough to finish on a cold cluster in
      minutes, large enough that the leaderboard has something to sort. Sizing
      it is a decision made once, in code, rather than a number an operator has
      to guess at first boot.
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

      **The synthetic source makes this gate exact rather than impressionistic.**
      Phase 2's generator has zero alpha by construction and asserts it, so
      *every* result over synthetic data is a false positive by definition, and
      the best of a large sweep over it is the strongest false positive the
      machinery can manufacture. Sweep it deliberately, take the winner, and
      require that the walk-forward number and the selection-adjusted figure
      both collapse toward nothing. This is a measurement with a known correct
      answer, which is not a thing the honesty layer could otherwise have had.
- [ ] **Commit:** "Trading: results that admit what they are"

### [ ] Phase 7 — The control panel

**Ships:** the ask's top-level interaction, and the point at which the vertical
becomes a product someone can look at. Strategies as the primary object, sweeps
launchable from the UI, one leaderboard — and, critically, **all of it populated
on first boot.**

That last part is the owner's requirement and it changes what this phase
delivers. A control panel deployed against an empty database is a shipped
*framework*: every screen renders an empty state and a button. What was asked
for is to deploy this on autopilot, open it in production, and find a working
product to react to. So the phase ships a seed job, and the strategies from
Phase 4 are its payload.

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
- [ ] **Data provenance is visible on every result** — a run, a leaderboard row
      and a strategy's headline number all show which source they came from,
      synthetic or Schwab. Not a footnote on a settings page. For as long as the
      app ships seeded with synthetic results, the difference between a number
      that means something and a number that means nothing is exactly this
      field, and a UI that hides it is a UI that lies by omission. It also stops
      being decorative the moment both sources coexist, which is every day after
      Phase 8.
- [ ] **The seed job** — a Kubernetes `Job` run on deploy, alongside the
      migration, that registers the two Phase 4 strategies and enqueues the
      Phase 5 demo sweep.

      **It computes the demo runs through the real engine and the real workers**
      rather than inserting fixture rows. Committed result rows would be a
      decoration that proves nothing and rots the first time a metric
      definition changes; computed ones prove the whole pipeline — queue,
      worker, engine, metrics, serializer — works in production, which is the
      thing an autopilot deploy most needs demonstrated. A cold boot therefore
      shows runs in flight for a minute or two before it shows results, which
      is a better demonstration than instant answers would be.

      **Idempotent, and namespaced to itself.** Seeded rows are marked as
      seeded; re-running the job reconciles only those. It must never touch a
      strategy, sweep or run the owner added, because the deploy that re-runs it
      is every deploy.
- [ ] Grafana carries deep-dive time series. Do not build a charting stack to
      compete with a tool already deployed and already good at this.
- [ ] **Gate:** deploy to a **clean production database**, open the control
      panel without running anything, and find every screen populated —
      strategies list, strategy detail with its parameter space, leaderboard
      with completed runs, and run detail with an equity curve · the seeded
      leaderboard shows `ma_crossover` losing to `buy_and_hold`, which is
      Phase 4's prediction holding in production · every result is visibly
      labelled synthetic · re-running the seed job changes nothing and destroys
      nothing · launch a sweep from the UI and watch it complete · the
      leaderboard answers "what did best today" in one glance, which is the
      ask's own success criterion · the app is correct in day and night mode ·
      `make test-web` green.
- [ ] **Commit:** "Trading: a control panel that arrives with something in it"

### [ ] Phase 8 — Schwab, and the seven-day problem

**Ships:** real market data. The `MarketDataProvider` interface gets its second
implementation, the collectors from Phase 3 are pointed at it, and option chain
history that cannot be bought back later starts accumulating.

**This phase may be promoted the moment approval lands.** It is placed eighth
because the owner asked that Schwab not appear until the rest of the stack is
deployed, and that ordering is the default. But it is written to be
*insertable*: it depends on Phases 2 and 3 and on nothing after them, so once
the lake and the collectors exist it can be pulled in ahead of any later phase
without disturbing them. The wall clock is the reason to use that latitude —
every day this waits is a day of option chains that has to be bought later or
done without — and the re-cut is what makes taking it cheap. If approval arrives
during Phase 4 or 5, promoting this is a judgment call the owner makes, not a
replan.

The 7-day refresh token is a **design constraint, not a defect to engineer
around.** Schwab requires periodic human re-authentication on purpose. A
collector that treats it as an error crash-loops weekly; one that treats it as a
scheduled event asks for thirty seconds of attention and keeps its history
intact.

**Budget for first contact.** Everything upstream of this phase was built
against clean generated data, so this is where the plan meets gaps, halted
symbols, splits and corporate actions, bad prints, zero-bid contracts, stale
quotes and DST seams — none of which the synthetic source models, by a decision
recorded in [Why the first data source is synthetic](#why-the-first-data-source-is-synthetic).
That decision was made knowingly and stands. What follows from it is that this
phase should be *estimated* as integration work rather than as a provider class:
assume the collectors need hardening they did not need before, and treat each
pathology found here as a candidate to fold back into the synthetic source as a
regression fixture, which is the cheapest moment to build the fault injection
that was deferred to get here.

- [ ] **First, verify and record in this document the facts this phase depends
      on.** All are widely reported and none should be built against unverified.
      Moved here from Phase 0a by the re-cut, because a fact verified months
      before it is used is a fact that gets verified twice:
      - Access-token lifetime (reported: 30 minutes) and refresh-token lifetime
        (reported: 7 days, human re-auth required, not programmatically
        renewable). The 7-day figure is the single most load-bearing unknown in
        the plan — this phase's whole shape depends on it.
      - Request rate limit (reported: ~120/minute per app).
      - Minute-bar history depth available from `pricehistory`, and daily-bar
        depth. This one now also sets how much history the engine has to work
        with on day one, since the lake holds no real bars before this phase.
      - Whether `/chains` returns greeks and implied volatility inline
        (reported: yes) — this decides whether Phase 10 computes them or merely
        validates them.
- [ ] **Seed the client id and secret** into the parameter store under the two
      paths Phase 0b already named, per
      [secrets-architecture.md](../secrets-architecture.md) — never in the repo,
      not even encrypted, per [ethos.md](../ethos.md). Flip `required` to true
      and add the `kubernetes` block, which is what 0b left for this phase to
      do.
- [ ] `providers/schwab/` — the OAuth 2.0 three-legged flow, with token storage
      in the secret store rather than a file in the pod. It implements the
      Phase 2 protocol unchanged; **if the protocol needs widening to fit
      Schwab, that is the finding**, and the change belongs in `base.py` with
      the synthetic implementation updated alongside it, not in a Schwab-shaped
      escape hatch.
- [ ] An operator-only re-auth page: one button that begins the flow, and a
      callback route that completes it. It sits behind the existing auth wall,
      so only an authenticated operator can complete an authorization — which
      is the correct security property and costs nothing to get. Phase 7 gives
      it a real UI to live in, which is one of the things this phase gains by
      running after the control panel rather than before it.
- [ ] Automatic access-token refresh (short-lived, silent). **Refresh-token
      expiry is surfaced, not retried**: a `token_expires_at` gauge on
      `/metrics`, a Grafana alert at T-24h, and a health endpoint that reports
      degraded rather than dead.
- [ ] Rate limiting client-side, below the verified ceiling, with backoff and
      jitter. One shared limiter for the whole silo — two collectors racing to
      the same quota is an outage.
- [ ] Record every provider call in `ingest_run`: what was asked, what came
      back, how long it took, what it cost against the quota.
- [ ] **The cutover.** Point the Phase 3 collectors at Schwab and set a real
      watchlist. Small and conservative to start; both are configuration.
      Synthetic and Schwab data coexist in the lake, distinguished by the
      `data_source` every row already carries, and no synthetic data is deleted
      — it is the reproducibility record for every run made before this day.
- [ ] **Demote the synthetic provider to test-only**, per the owner's decision:
      it stops being a deployable in-cluster provider and remains available to
      `pytest` and to a local development run. **Triggered by the cutover being
      stable, not by this phase starting** — the offline test loop and the
      seeded demo are load-bearing right up until real data is flowing reliably,
      and demoting it on day one of this phase would remove them mid-flight.
      Phase 6's overfitting gate keeps using it forever; that is the one place
      the generator is not a stand-in but the correct instrument.
- [ ] **Gate:** a chain and a bar series are fetched from production Schwab and
      written to the lake by the *unmodified* Phase 3 collectors · the access
      token refreshes across a 30-minute boundary without intervention · the
      T-24h alert fires against a simulated expiry · a deliberately expired
      refresh token produces a degraded readiness state and an alert, not a
      crash loop · a full real session collects end to end and its gaps are
      explained rather than merely absent · the control panel shows real and
      synthetic results side by side, correctly labelled.
- [ ] **Commit:** "Trading: real data, and a credential that expects to expire"

### [ ] Phase 9 — The live clock

**Ships:** paper trading. Strategies run forward against live quotes, and the
leaderboard gains a column that changes during the day.

The live clock is provider-agnostic like everything else, so it can be developed
and demonstrated against the synthetic source without waiting for a market to be
open — but it is placed after Phase 8 because a paper session against noise is a
curiosity, and against real quotes it is the point.

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

### [ ] Phase 10 — Options

**Ships:** the actual target. Chain-aware strategies, multi-leg positions, and
the three strategy families the owner named.

This is the largest phase and should be split into its own directory under
[`docs/plans/`](README.md) when it starts, per the lifecycle in the plans
README. What follows is its shape, not its detail.

**This phase requires real chain data and is refused synthetic chains in code**,
by the guardrail Phase 2 installs. Synthetic chains are structurally valid and
numerically meaningless; an option backtest against them would produce a
confident wrong number, which is the exact failure mode Phase 6 exists to
prevent. Nothing here can start until Phase 8 has been collecting for a while,
and that is the plan's one remaining irreducible wait.

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
      skew. Depends on chain history depth, which is why Phase 8 is promotable
      the day approval lands: this phase is the one whose start date is set by
      how long real chains have been accumulating.
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
- **Fault injection in the synthetic source.** Deliberately emitting gaps,
  duplicates, out-of-order rows, halts and splits, so that the collectors meet
  those pathologies before Schwab does. Considered and deferred by the owner to
  Phase 8, on the reasoning that real data should say which faults actually
  occur rather than guessing at them. The cost is carried as a named risk in
  Phase 8's estimate; the cheapest time to build it is as each real pathology is
  found there, as a regression fixture.
- **A realistic synthetic source.** Implied-volatility surfaces, jumps, regime
  switching, microstructure. The generator is pure noise permanently and on
  purpose — as a control it is *more* useful for being unrealistic, and as a
  stand-in for real data it stops being needed at Phase 8.

## Verification

The plan is done when these are all true, in this order:

1. The vertical deploys on its own and arrives demonstrating itself: a clean
   production database, a cold boot, and every screen of the control panel
   populated without anyone launching anything.
2. Option chain history has been accumulating for long enough to backtest
   against — the only item on this list that cannot be accelerated by working
   harder, which is why Phase 8 is written to be promoted the day approval
   lands rather than waited for in order.
3. A strategy can be added by writing one file and deploying, exactly as the ask
   assumes — and the two shipped strategies are what an operator reads to learn
   how.
4. A sweep over that strategy's declared parameters runs without the cluster
   noticing.
5. The leaderboard answers "what is doing best right now" in one glance, across
   backtests and live paper sessions together, with the source of every number
   visible on its face.
6. A deliberately overfit strategy is visibly ranked as such — measured against
   the synthetic source, where the correct answer is known.
7. Swapping the data source is a class and a configuration value, demonstrated
   by there being two of them and no collector code that knows which is running.
8. Nothing under `src/Aerie.Trading/` imports from `Aerie.Api`, and CI proves it
   on every commit.
