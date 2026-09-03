# Trading — a strategy laboratory that rides Aerie as a platform

**Status:** Phases 0b through 5 built on 2026-09-02; Phase 0a is waiting on
Schwab. What is left of Phase 1 is its gate — five checks that need the cluster
— plus one line an operator adds to the site repository, both written out under
that phase. Phase 2's and Phase 4's gates passed in full. Phase 3's passed but
for the two checks that need a running cluster to observe, and Phase 5's but for
the one that does; all three are cases of the same argument for having built
them without one, and Phase 5 records what was measured in place of the check it
could not make.

Phase 5 is also where the suite stopped being able to run with no database at
all: the run queue is a Postgres queue, so `ci.yml`'s trading lane now carries a
Postgres service container and `make trading-test-db` starts a throwaway one
locally. `make trading-test` is still green on a box with nothing installed — it
simply leaves that phase's gate unasserted.

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
story is its own. Raw market data is also the one dataset here that is, in
principle, re-collectable from the source. Option chains are the exception, and
that exception is what makes their backup worth doing before the lake is large.

**Built at Phase 3, and only for `chains/`.** A nightly restic job writes that
subtree to the same two repositories the rest of the house uses — off-site S3
and the local share — through a generic
`containers/backup/scripts/path-backup.sh` that takes a path and knows nothing
about trading. `bars/` is deliberately outside it: a backfill already protects
them, and including them would roughly double what the job moves to protect
something that is re-derivable.

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

### [x] Phase 2 — The synthetic market

**Ships:** the `MarketDataProvider` interface and a generator that implements
it, so that every phase after this one has data to work on without a credential,
a network call or a market being open. This is the phase that decouples the rest
of the plan from Schwab.

It is deliberately trivial. The generator is **pure noise, permanently** — it
never grows an implied-volatility surface, a jump model or a regime switch,
because the moment it becomes interesting it stops being a control and starts
being a thing whose own behavior has to be reasoned about. Its job is to have
the right *shape*, not the right *statistics*.

**Built 2026-09-02.** Every bullet below is committed and the gate passed in
full — no part of it needs a cluster, which is the argument for the re-cut
restated as a fact. Five things came out of building it and are recorded in
place rather than as a footnote: the `data_source` table had nowhere to record a
seed (below), the daily and intraday walks deliberately do not reconcile
(below), the option-backtest refusal is a provider property plus a guard rather
than a check at a call site that does not exist yet (below), the universe is
Nasdaq's own test tickers (below), and `exchange_calendars` brings pandas, which
is 142 MB of image and a package that must not be imported by the migration init
container (below).

- [x] `providers/base.py` — the `MarketDataProvider` protocol: `quotes()`,
      `bars()`, `chain()`, `market_hours()`. **Written here rather than
      alongside Schwab on purpose.** A protocol whose only implementation is a
      vendor API ends up encoding that vendor's quirks as though they were the
      shape of market data; this one has to satisfy a second implementation
      before it hardens, and the trivial one is the better first because it can
      be bent to the interface rather than the reverse.

      Two decisions the writing forced, both of which a Schwab-first draft
      would have got wrong. **Every call names the instant it asks about**,
      including `quotes()`, which a live vendor only ever answers for "now" —
      that argument is what lets `ReplayClock` and `LiveClock` drive the same
      code at Phases 4 and 9, and a live provider satisfies it by refusing an
      `as_of` that is not approximately now. And **`market_hours()` is answered
      from `exchange_calendars`, not from the vendor**: a session's boundaries
      are a property of the exchange rather than of whoever is reporting
      prices, and a provider answering out of its own head would be a second
      opinion about a fact.
- [x] `providers/synthetic/` — the generator. **Stateless and deterministic:** a
      bar is derived from a hash of `(seed, symbol, timestamp, interval)` rather
      than from a stored path, so the same request returns the same bar forever,
      no generated series has to be persisted, and a backfill and an incremental
      collection of the same window agree by construction. That last property is
      what Phase 3's idempotency gate is actually testing, and this makes it
      testable without a network.

      `providers/synthetic/noise.py` is where that property actually lives:
      blake2b turns a coordinate tuple into a stream key and splitmix64 turns a
      `(key, index)` pair into a value, so a draw has an address rather than a
      position. A seeded `random.Random` cannot do this — its output depends on
      how many values were drawn before it, so two callers asking for
      overlapping windows would get different prices for the same minute.
      Python's own `hash()` cannot either: it is salted per process, which is a
      determinism bug that appears only *between* the collector pod and the
      worker pod, and never in a test. Both are asserted; the second by running
      two interpreters under different `PYTHONHASHSEED`s. Verified further by
      generating the same bar on macOS/arm64 and inside the linux image and
      diffing the two.
- [x] Bars: a random walk per symbol — open, high, low, close, volume, with the
      OHLC relationships internally consistent, because a collector or an engine
      that trips over `high < close` should trip over real data, not over the
      fixture.

      `Bar` rejects a violation in its constructor rather than trusting the
      generator not to produce one, and the generator makes the invariant
      arithmetic rather than probabilistic: prices are rounded to cents *first*
      and the extremes clamped afterwards, because rounding four numbers
      independently can otherwise put a close a cent above its own high.

      **The one non-property worth stating out loud: the daily bar and the
      intraday bars of the same session share an opening price and nothing
      else.** They are separate walks, so the last five-minute close of a
      session is not the daily close. Making them agree needs a Brownian
      bridge — generate the intraday path, then pull it onto the session's
      known terminal price — and a bridge's increments are negatively
      correlated *by construction*, because their sum is constrained. Trading a
      guaranteed structural defect for a cosmetic agreement between two views
      of a fictional price is the wrong side of that trade when zero
      exploitable autocorrelation is the single property this source exists to
      have. Nothing in the plan needs the agreement: Phase 3 writes each
      interval to its own partition and Phase 4 runs one interval per backtest.
- [x] Chains: **structurally valid, numerically meaningless.** A plausible
      strike ladder and expiry calendar, the full row shape Phase 3's partition
      layout expects, and noise in every price, greek and IV field. This is
      enough to exercise the collector, the partition scheme, the DuckDB reader
      and the health metrics — the plumbing, which is all Phase 3 is about.

      The structure that *is* honest, and only because a collector tripping
      over it should be tripping over real data: the strike increment follows
      the price level, expiries are weekly and monthly Fridays that fall back
      to the previous session when the Friday is a holiday, the contract symbol
      is spelled the way the OCC spells it, bid never exceeds ask, gamma and
      vega are non-negative, theta is non-positive, and delta carries the sign
      and range its right implies. One line looks like modelling and is not: a
      contract's price is floored at its intrinsic value, which is
      `max(0, spot - strike)` on two numbers already in the row — arithmetic,
      not a pricing model, and without it the board carries contracts trading
      below their own exercise value, a shape no venue produces.

      **The guardrail:** every row the lake stores already carries its
      `data_source`, and an option backtest against a synthetic source is
      **refused** — in code, the way Phase 6 refuses to serve an in-sample-only
      figure, not by convention and not in the UI. Noise chains are fine for
      moving bytes and meaningless for pricing anything, and the distance
      between those two is exactly where a plausible-looking wrong answer would
      come from. Phase 10 is unaffected: it lands after Phase 8, so real chain
      history exists by the time anything wants to backtest against one.

      **How it is built, since the thing it guards does not exist yet.** The
      claim is a property on the provider (`chains_are_priceable`, `False`
      here), the refusal is `require_priceable_chains()` in `providers/base.py`,
      and the two are joined by the provenance blob: the claim is written into
      the `data_source` row and `chains_are_priceable_in()` reads it back, so
      the check also works from the far side of the lake — where all that
      survives of a provider is a row. It **fails closed**: a provenance that
      does not say reads as not priceable, because the rows a collector wrote
      before the key existed cannot vouch for themselves. What is *not* built
      is the call site, because Phase 4's engine and Phase 10's option
      backtests are the things that would call it. Those phases wire it; this
      one makes wiring it a one-line import rather than a design question.
- [x] **The calendar is real even though the prices are not.** The generator
      honors `exchange_calendars` — same session boundaries, same holidays, same
      early closes. A synthetic session that runs 24/7 would leave Phase 3's
      calendar handling completely unexercised, which is the one piece of Phase
      3 that fails silently rather than loudly.

      `providers/calendar.py` is the only module in the silo that touches
      `exchange_calendars` or pandas, and the containment is doing two jobs.
      *Typing:* neither library ships type information, so under this project's
      strict pyright every value out of them is Unknown — the suppressions are
      scoped to one adapter whose exports are all builtins, rather than being a
      global loosening. *Cost:* the session boundaries are read out of pandas
      once at construction into plain tuples, because a price is derived by
      walking every session since the anchor and a pandas lookup inside that
      loop would be the whole runtime.

      **And the cost that is not contained: `exchange_calendars` brings pandas
      and numpy, which took the image from 214 MB to 356 MB.** Unavoidable —
      it is the library this plan chose Python for. What *is* avoidable is
      importing it in a process that only wanted the seed, so
      `providers/synthetic/__init__.py` deliberately re-exports nothing:
      `Settings` carries a `SyntheticConfig`, so every process in the silo
      imports `.config`, including the migration init container that runs under
      a 256 Mi limit. A convenience re-export there would have put half a second
      and something like a hundred megabytes into a process whose entire job is
      `alembic upgrade head`.
- [x] Configuration: universe, seed, starting price level, drift and volatility,
      all values rather than code. Drift defaults to zero.

      A `SyntheticConfig` model on `Settings`, so an installation re-seeds with
      `TRADING_SYNTHETIC__SEED` or replaces the universe wholesale with JSON in
      `TRADING_SYNTHETIC` — values rather than code means an operator can change
      them without a rebuild, or it means nothing. The drift is on the *log*
      price, which is what makes "zero" mean "the log price is a martingale".

      **The universe is `ZVZZT` and its siblings, which are Nasdaq's own
      reserved test tickers.** A synthetic universe named SPY and AAPL is a
      loaded gun: a screenshot of a leaderboard, a row in a lake partition or a
      figure quoted out of context reads as a claim about the real instrument,
      and nothing downstream can tell the difference. These cannot be mistaken
      for anything, which puts the guardrail in the data itself rather than only
      in a `data_source` join. One of them carries no options board, so that
      "this symbol has no chain" is a case the collector meets here rather than
      at Phase 8.
- [x] **Zero alpha, asserted.** A test establishes that generated returns carry
      no exploitable autocorrelation at the sample sizes the plan uses. Phase 6's
      gate rests on this being true, so it is a test rather than a claim — if the
      generator ever acquires structure, the phase that depends on it should
      break loudly here rather than quietly there.

      `tests/test_synthetic_zero_alpha.py`, and it is the load-bearing file in
      the phase. The threshold is three standard errors under the null that the
      returns are independent — `3 / sqrt(n)` — measured at lags 1 through 10,
      across the whole universe, across three seeds, over every session the
      calendar holds. The intraday walk is measured separately, because it is a
      separate process and because it is the test that would fail the day
      somebody bridges it. Every input is deterministic, so a pass is a fact
      about the generator rather than a lucky draw, and a failure is a
      regression rather than flakiness. The measured worst case sits at 77% of
      the threshold.
- [x] Registered as a `data_source` row, with the seed and configuration
      recorded, so a run is reproducible from its provenance alone.

      **This needed a column Phase 1 did not build.** `data_source` had name,
      description and a flag, and nowhere to put a seed — so migration
      `0002_data_source_config` adds a nullable `config` JSONB, arriving with
      the phase that has something to write into it rather than with the phase
      that would have guessed at its shape. `ensure_data_source()` is idempotent
      by name, because every process that touches a provider registers at
      startup and the second arrival must not be a unique-constraint failure or
      a second row that half the data then points at. A test rebuilds the
      provider from the recorded blob and asserts it produces the same bars,
      which is the only honest test of "reproducible from its provenance alone".

      **The second migration also broke the test that guards the first**, in the
      silent direction: `tests/test_migrations.py` compared rendered `CREATE`
      statements, an `ALTER TABLE ... ADD COLUMN` is not a `CREATE`, so the new
      column was invisible to both sides of the comparison and the schemas
      matched for the wrong reason. It now reduces the DDL to a schema — tables
      as sets of column and constraint definitions, with the ALTERs folded in —
      and *raises* on any statement form it was not taught, so the next person
      to write an `ALTER COLUMN` is told to extend it rather than reassured by
      it.
- [x] **Gate:** bars and chains are produced for a configured universe · two
      identical requests return byte-identical data · a full simulated session
      generates in CI in seconds with no network · generated returns show no
      exploitable autocorrelation · the calendar refuses to generate a session
      on a market holiday.

      All five pass, in `make trading-test`, with no cluster and no network —
      which is the re-cut's whole thesis discharged. The third is measured
      rather than asserted by inspection: a full session of minute bars for the
      whole universe generates in 0.12 s, and the test's bound is 5 s, since it
      exists to catch a change that makes the walk quadratic rather than to be
      a benchmark. The fifth is checked at both levels — the calendar refuses to
      index a holiday, and `quotes()` and `chain()` on one raise rather than
      returning empty, because an empty answer is indistinguishable from a
      market that was open and silent.
- [x] **Commit:** "Trading: a market that does not exist"

### [x] Phase 3 — The lake, and the collectors that fill it

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

**Built 2026-09-02.** The acceptance criterion holds: nothing under
`aerie_trading/collect/` or `aerie_trading/lake/` names a provider, and the one
place that constructs one is `collect/__main__.py`'s `build_provider`, which
Phase 8 extends by a branch. Six things came out of building it and are
recorded in place rather than as a footnote: the default storage class is the
wrong volume for this data (below), DuckDB renders timestamps in the *host's*
timezone (below), cron cannot express the chain schedule (below), the closing
board was being asked for at an instant the market was shut (below),
`ingest_run` had to widen from a provider call to a collection run (below), and
the chain backup rides the house's existing restic pair rather than a second
one (below).

- [x] A PVC on the default storage class, modest to start. The **lake root is a
      config parameter**, so relocating it later is a value change and not a
      code change.

      `TRADING_LAKE_ROOT`, `/lake` in the cluster and a relative `.aerie-lake`
      outside it — defaulted to something *wrong* for a pod on purpose, so a
      volume that failed to mount fails visibly instead of quietly writing onto
      an ephemeral root filesystem and reporting success for a week.

      **Not the default storage class, and the deviation is the point.**
      Longhorn's chart sets `defaultClass: false`, so an unqualified claim on
      this cluster gets k3s' `local-path`: a directory on one node's disk, no
      replica, ReadWriteOnce *and* node-pinned. That is wrong for this data
      twice over — losing the node loses the lake, and the collectors could
      only ever run on whichever node the volume first bound to, which is the
      "special case a node" shape this house does not build. It is
      `longhorn-r2` and **ReadWriteMany** instead: three workloads mount it
      (two collector CronJobs, and Phase 4's engine), Longhorn serves RWX
      through a share-manager over NFS, and `nfs-common` is already on every
      node because `Initialize-NodeStorage.ps1` installs it for exactly this.
      RWO would have worked by accident until the scheduler put the chain
      collector somewhere else, at 15:00 on a weekday.

      **32Gi, which is a measurement.** See the sizing gate below.
- [x] Lake layout, written down before it has data in it, because rewriting a
      partition scheme is a migration:
      - `bars/{interval}/{symbol}/{year}/{month}.parquet` — OHLCV, UTC
        timestamps, adjusted and unadjusted close both stored.
      - `chains/{underlying}/{date}/{hhmm}.parquet` — one row per contract per
        snapshot: strike, expiry, right, bid, ask, last, volume, open interest,
        and the greeks/IV as the provider reported them.
      - Every file carries the provider, the collection timestamp, and the
        `aerie-revision` of the collector that wrote it.

      Exactly as written, in `lake/layout.py`, with two things the writing
      decided. **The provenance is per row, not per file** — Parquet
      dictionary-encodes a column holding one repeated value down to almost
      nothing, and a file-level key/value carries no further than the file: the
      moment the reader concatenates twelve partitions into one frame, "which
      build wrote this row" would be a filesystem question again, which is the
      one thing the reader exists to prevent. It is also what makes the
      re-collection case legible — a month partition can legitimately hold rows
      from two builds, and per-row stamps are the only way to tell which is
      which. **And the column is `option_right`, never `right`**, matching the
      Ledger: `RIGHT` opens a `RIGHT JOIN` in DuckDB exactly as it does in
      Postgres, and one name across both means neither needs quoting in a
      hand-written query during an incident.

      A symbol is refused rather than sanitised if it could name a path outside
      the root. It arrives from a configured watchlist or a provider response,
      so it is not this process's own string; stripping the offending
      characters would turn a typo into a partition that looks legitimate and
      holds another instrument's data.
- [x] `lake.py` — a DuckDB reader with one job: turn a symbol and a time range
      into a polars frame, hiding the partition layout from every caller. The
      engine must never learn the directory structure.

      A package, `lake/`, rather than one module — `layout.py`, `schema.py`,
      `writer.py`, `reader.py` — split on how expensive each is to import
      rather than on taste. `layout.py` is stdlib only, which is what lets the
      *migration init container* reach `Settings` without paying for polars;
      the same containment argument `providers/synthetic/__init__.py` makes,
      and measured: the control plane loads neither polars nor duckdb, and only
      the collector CLI loads polars.

      **The finding worth the phase's time: DuckDB renders `TIMESTAMP WITH TIME
      ZONE` in its *session* timezone, which defaults to the host's.** The same
      query returns `datetime[us, America/New_York]` on a laptop and
      `datetime[us, UTC]` on a cluster node. The instants agree and the dtypes
      do not — which is a schema mismatch in a test that passes in CI, or a
      comparison against a naive datetime that silently shifts by five hours.
      `SET TimeZone='UTC'` at construction, and a test that sets `TZ` to a
      third zone so a runner that happens to be UTC cannot hide it.

      **The file list is computed, never globbed.** `layout.py` can name every
      partition a request could touch, so the reader asks the filesystem only
      whether those exist and hands DuckDB the survivors. That is what keeps
      "nothing was collected here" an empty frame with the right columns rather
      than the `IO Error: No files found` DuckDB raises for an empty glob —
      which would make an un-collected range and a mistyped symbol the same
      exception. It also means a writer's leftover temporary file can never be
      swept into a read.

      DuckDB hands rows to polars over the Arrow C stream PyCapsule interface,
      so the two convert with no copy and **without pyarrow** — `relation.pl()`
      goes through `pyarrow.Table` and would have put a 45 MB wheel in the
      image to do a conversion both libraries already do natively.

      **The cost that is not contained: polars and duckdb took the image from
      356 MB to 617 MB.** Same shape as Phase 2's `exchange_calendars` finding
      and the same answer — the containment is on which *processes* pay for it,
      not on the image. Verified in the built image: the migration init
      container imports `Settings` under its 256 Mi limit and loads none of
      polars, duckdb, pandas or numpy; the control plane loads none of them
      either, because collection health is a Postgres query rather than a lake
      read. Only the collector CLI loads polars, and only the reader loads
      duckdb. A second, smaller image for the migration would trade that
      against two images to build, tag, scan and keep in step, which is the
      worse side of the trade at this size.
- [x] Idempotent writes. A re-run over the same window overwrites its own
      partition and does not append duplicates. Assume the collector will be
      re-run; make that boring.

      **Two properties, two mechanisms, and conflating them would have got one
      of them wrong.** Idempotency is an *anti-join*, not an overwrite: a bar
      partition is a month and a collection is a session, so "overwrite the
      partition" would delete twenty sessions to rewrite one, every weekday.
      The rows the incoming frame names are replaced and everything else is
      left alone, which makes the backfill and the incremental collector the
      same code path with different arguments — and that is what the gate's
      agreement test actually checks.

      Crash-safety is an *atomic rename*: every write goes to a temporary file
      in the destination's own directory, is fsynced, and is moved onto the
      destination with `os.replace`. A process killed at any point leaves
      either the previous file or the new one, never a truncated Parquet
      footer — which matters more than it sounds, because a truncated footer
      would take the partition out *permanently* rather than for one run: the
      re-run would fail reading it.
- [x] **Market calendar** via `exchange_calendars`, consulted before every
      collection. Do not collect through a holiday and record silence as data.

      And the distinction that rule needs to be useful: a *range* with no
      sessions in it is not an error — a backfill over a holiday week is a
      legitimate request with a legitimate empty answer — but a session inside
      the range that produced no bars **is a gap** and is recorded as one.
      "There was no market" and "there was a market and we have nothing from
      it" are different mornings.
- [x] `collect/bars.py` — daily and minute bars: a backfill mode for whatever
      depth the configured provider offers, and an incremental mode after each
      close.

      Three modes, not two. `incremental` is *today's* session and refuses a
      day the market was shut; `latest` is the most recent session whenever it
      was, which is catching up by hand; `backfill` is a range. The CronJob
      runs `incremental` deliberately — a holiday run that quietly re-collected
      yesterday would exit 0 and advance the last-success metric over a day
      nothing was collected, which is the same failure the calendar rule exists
      to prevent, wearing a different hat.

      The provider is asked for **one month of one symbol at a time**, which is
      the lake's own partition granularity: one call fills exactly one file, so
      an interruption lands between whole partitions rather than inside one.
- [x] `collect/chains.py` — **the flagship.** Snapshot the full chain for a
      configured watchlist on an interval through the session. Start with a
      small watchlist and a conservative interval; both are configuration, and
      widening them later costs nothing while starting late costs everything.
      It collects noise today and real chains the day Phase 8 lands, and the
      code does not know the difference — which is the whole point of building
      it now.

      Thirty minutes and the four option-bearing test tickers, both
      configuration (`TRADING_COLLECTION__*`). Two decisions the writing
      forced:

      **A watchlist entry with no board is a gap, not a failure.** The other
      three names are still snapshotted and the missing one is recorded on the
      run. Raising would mean one stale watchlist entry stops collecting
      everything else for the rest of the session, which is a far larger loss
      than the thing it reports. Phase 2 put a symbol with no options board in
      the default universe precisely so this path is met here rather than on a
      Schwab-approval morning — and it is met, in a test.

      **The closing board is taken at 15:59, not at 16:00, and that was a
      defect before it was a decision.** `MarketSession.contains` is half-open,
      so the schedule's closing snapshot was asking the provider for an instant
      the market was not open for: every session's last board was a refusal
      recorded as a gap. Caught by the full-session gate test rather than by
      inspection. 15:59 is also the right answer rather than merely a working
      one — it is the instant the session's last minute bar opens at, so the
      closing board and the closing bar describe the same minute, which is what
      makes an as-of join between them mean anything at Phase 10.
- [x] Scheduling: a `CronJob` per collector, or one scheduler process — chosen
      on which is easier to reason about when a collection is missed, not on
      elegance. The missed-collection case is the one that matters.

      **CronJobs**, on that tie-breaker and no other. A missed run is a Job
      that does not exist, and `kube_cronjob_status_last_successful_time` is
      already scraped; a scheduler process's missed tick is invisible, because
      the thing that would have logged it was not running. The schedule also
      survives the process — a scheduler restarted at 15:59 has to decide for
      itself whether it already ran the 15:30 collection, and the kubelet has
      that written down — and `concurrencyPolicy: Forbid` is a field rather
      than a lock.

      **What it costs is schedule expressiveness, and the bill came due
      immediately: cron cannot say "every thirty minutes from 09:30 to
      15:59".** `*/30 9-16` fires three times a day outside the session and
      still never captures the closing board. So the chain collector is *three*
      CronJobs — open, interval, close — each firing only inside a regular
      session, for thirteen snapshots a day and zero routine refusals. A
      refusal should mean something, and a schedule producing three failed Jobs
      a day by construction is one whose failures nobody reads.

      `timeZone: America/New_York`, which is the only use of that field in
      `deploy/`. Everything else in the tree is scheduled against other things
      on these nodes and stays in UTC for the stagger; these are scheduled
      against *the exchange*, whose close moves an hour in UTC twice a year and
      never moves in its own zone. A UTC expression would have collected an
      hour early for four months of the year — which for `--mode incremental`
      means collecting a session that is still open.

      A market holiday still produces fourteen failed Jobs, because cron knows
      about weekdays and not about Good Friday. That is stated in the manifest
      rather than left to be discovered, and nothing alerts on it: the alerts
      read the Ledger-derived series, which know what a session is.
- [x] Collection health is visible: rows written, gaps detected, last successful
      run per collector, all on `/metrics`, with an alert for a session that
      collected nothing.

      **Derived at scrape time from `ingest_run`, not pushed by the
      collectors**, and the obvious implementation cannot work: a collector is
      a CronJob pod that runs for thirty seconds and exits, so there is nothing
      alive to scrape, and a push gateway would be a second piece of
      infrastructure to run and to reason about staleness in. The Ledger
      already has a durable row per run, the control plane is already scraped,
      and the join between them is one query. Nothing new is deployed to make
      collection health visible.

      The consequence is that these series are exactly as available as the
      trading database — so there is a `trading_collection_up` gauge beside
      them, and a scrape that cannot reach the Ledger answers `0` rather than
      failing. A failed scrape looks identical to a dead pod, and every
      staleness alert would fire at once saying the wrong thing.

      The alert the phase asks for is `TradingCollectionEmpty`, on
      `trading_collection_last_success_rows == 0`: a run that *succeeded* and
      wrote nothing is invisible in every other series — the Job exited zero,
      the Job history is green, the last-success timestamp is current, and the
      lake gained nothing.

      **`ingest_run` widened, and the plan should say so.** Phase 1 described
      it as "one call out to a provider". A row is now one *collection run*,
      which may make many provider calls: a chain snapshot of a four-name
      watchlist is four calls, thirteen times a session, five days a week —
      a thousand rows a month recording something nobody asks a question at
      that granularity about. Migration `0003_ingest_run_result` adds
      `gap_count` (the one fact that is aggregated, so a plain integer rather
      than a `jsonb_array_length`) and `result` (the counterpart to `request`:
      that column says what was asked for, this one says what came back and
      what was missing).
- [x] A backup path for `chains/` specifically — the one dataset here that
      cannot be re-collected. **Built now and left running against synthetic
      data**, so that the first chain snapshot worth keeping is already inside a
      backup path that has been exercised, rather than being the run that tests
      it. Synthetic chains are worthless and backing them up is nearly free;
      the point is that the mechanism is proven before the data is precious.

      A nightly CronJob at 23:30 UTC running **Aerie's own backup image**
      against a new generic script, `containers/backup/scripts/path-backup.sh`,
      which takes a path, a snapshot host and a tag and knows nothing about
      trading. That is the platform contract working rather than a coupling —
      the silo consumes Aerie's backups the same way it consumes CNPG and
      Traefik — and it is the productization story: "I have a PVC holding
      something that cannot be regenerated, put it in the backup path" now has
      an answer that is not trading-specific.

      **The same two restic repositories the house already has**, not a new
      pair. A second pair would be two more things to create, scope, pay for
      and rehearse restores from, and one credential already reaches both.
      Reaching the local SMB repository from a second namespace needs a second
      `PersistentVolume` object with its own `volumeHandle` — two PVs sharing a
      handle are one volume to the CSI layer, and the second mount silently
      inherits the first one's options.

      **Retention is applied by the aerie-backup job, deliberately.** `restic
      forget` groups by host and paths; these snapshots carry their own host
      (`trading`) and path, so they form their own group and that job's
      7/4/12 policy lands on them. Retention across a repository must be owned
      by exactly one job — two `forget --prune` writers is how a snapshot
      disappears with both jobs reporting success — and 7/4/12 is the right
      policy for an *append-only* tree: nothing is ever deleted from `chains/`,
      so the newest snapshot always contains every file, and ageing out an old
      one discards a view of the tree rather than the only copy of anything in
      it. The day something does delete from `chains/`, that reasoning stops
      holding, and it is written in the manifest so it can be noticed.

      The credential arrives as a `trading` target block on each of the three
      `/aerie/backup/*` parameters in `scripts/secrets/parameters.json`;
      `New-ExternalSecrets.ps1 -Check` is in sync.
- [x] **Gate:** a full simulated session collected end to end with no gaps · a
      DuckDB query returns a chain snapshot as a polars frame in reasonable time
      · a deliberately killed mid-collection run leaves no duplicate rows on
      re-run · a collection attempt on a market holiday is refused rather than
      recording silence as data · the whole session collects in CI without a
      network · lake size per session measured and extrapolated **at the row
      counts a real chain implies, not the synthetic watchlist's**, so the PVC's
      lifetime is a number rather than a hope.

      All six pass in `make trading-test`, with no cluster and no network. Each
      is a named test rather than an inspection:

      - *A full session, no gaps.* Bars and chains both, end to end into the
        lake and back out through the reader. This is the test that caught the
        closing-board defect above.
      - *A chain snapshot in reasonable time.* Measured at roughly 5 ms; the
        bound is 1 s, because what it exists to catch is a change that turns a
        named-file read into a directory walk — orders of magnitude, not
        percentages.
      - *Killed mid-collection.* A four-symbol collection is interrupted while
        writing the third partition and then re-run from the start; afterwards
        every partition holds exactly one copy of every bar. Two narrower tests
        beside it assert the previous file is still readable and that no
        `.tmp` files are left in the tree.
      - *A holiday is refused.* Checked at both collectors and on both Good
        Friday and Christmas, and the assertion is that **no run was recorded
        at all** — a refusal is not a collection that failed.
      - *No network.* Asserted rather than assumed from CI's environment: every
        socket constructor is replaced with one that raises. This is the test
        that will fail the day somebody wires an HTTP client into the chain
        collector without noticing the suite is meant to be offline.
      - *Lake size.* `tests/test_lake_size.py` measures bytes-per-row against a
        board of ~6,500 contracts — a real board's size, not the default
        universe's 204, because Parquet's fixed footer dominates a small file
        and would flatter the figure threefold. **34.6 bytes per contract row**,
        extrapolated to a reference watchlist of two index-scale boards and
        eight large-cap ones at fourteen snapshots a session, 252 sessions a
        year: **5.75 GiB a year, so the 32Gi volume holds 5.6 years.** The test
        asserts that figure clears three years, so widening the watchlist far
        enough fails CI rather than filling a volume quietly, and it prints the
        number under `pytest -s` so the manifest can be re-derived.

      **Two things need the cluster and are not claimed here:** that the RWX
      volume actually binds and that all four CronJobs fire on their schedules.
      What was verified in their place: every kustomization under `deploy/`
      builds, every substitution token resolves against `cluster-config.json`,
      the five `trading-boundary` guards pass, `New-ExternalSecrets.ps1 -Check`
      is in sync, and the collector CLI's argument grammar — the contract
      between the manifests and the package — is tested including the refusal
      exit code. The image was built and exercised with `--network none`: it
      collects a session of bars and a chain snapshot into a lake and reads
      both back with the right dtypes, `alembic upgrade head --sql` renders the
      new migration from inside it under a 256 Mi limit, and
      `python -m aerie_trading.collect chains --session <a holiday>` exits 2
      with a correctly-shaped JSON log line.
- [x] **Commit:** "Trading: a lake, and the machinery to fill it"

### [x] Phase 4 — The engine, proven on equities

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

- [x] `engine/clock.py` — the `Clock` protocol and `ReplayClock`. `LiveClock` is
      Phase 9 and must require no change here when it arrives. Three members:
      `now`, `advance()` and `ticks()`. `advance()` returns the new instant
      rather than a bool, because a live clock blocks until the next tick and
      then knows what time it is; `None` distinguishes "the data ran out" from
      "time has not moved". `now` raises before the first advance rather than
      answering with the first instant, so a loop nobody started is an error
      instead of a number.
- [x] `engine/instruments.py` — `Equity` and `OptionContract` (underlying,
      expiry, strike, right, multiplier) behind one `Instrument` type.
      **Written in this phase even though only `Equity` is exercised**, because
      this is the retrofit the plan exists to avoid. A **union rather than a
      base class**, so a consumer that must handle both is checked for having
      handled both; the OCC contract symbol is derived rather than carried, so
      a contract built from a chain row and one built by a strategy's own
      arithmetic key the same position.
- [x] `engine/money.py` — **the boundary where a price stops being a float.**
      Not in the original list and needed by everything below it. The lake
      stores `Float64` because Parquet and DuckDB do; a gate asserted *to the
      cent* cannot be met by a running total that accumulates binary error one
      fill at a time. Prices become `Decimal` once, through `repr` so that a
      provider's `4.56` is `Decimal("4.56")` and not its binary neighbour; cash
      is quantized to the cent at each movement, banker's rounding, because
      half-up is biased and ten thousand trades through a half-cent is an
      invisible edge in the trader's favour.
- [x] `engine/portfolio.py` — `Position` as a set of **legs**, cash, mark-to-
      market, realized and unrealized P&L. Single-leg equity positions are the
      degenerate case, not the model. A fill names the position it belongs to;
      when it does not, the key is the instrument's own symbol — which is what
      makes the equity case free and leaves Phase 10 a spread that is one
      position because its legs were submitted under one key. Weighted-average
      cost, commissions **expensed rather than capitalised** so that a hand
      check reads the basis straight off the trade prices, and one signed
      expression for realized P&L that is correct for shorts without a second
      branch.
- [x] `engine/broker.py` — the `Broker` protocol and `SimBroker`: fills at the
      next bar's open by default, a configurable slippage model, and a
      commission model. **No same-bar fills on the signal bar** — that single
      shortcut is the most common source of backtests that cannot be
      reproduced live. Enforced structurally rather than by convention:
      `submit()` queues, `fill_at()` is called by the engine *before* the
      strategy runs, and there is no code path from `on_bar` to a fill on the
      same bar. Costs are two models rather than one haircut, because they
      scale differently and Phase 6 varies one of them; `PerUnitCommission`
      covers both a per-share equity schedule and a per-contract option one,
      since the difference between them is the multiplier and that is on the
      instrument. **The defaults are not zero** — a backtester whose default
      costs are zero is one whose default answer is optimistic — and
      `ZERO_COSTS` is named so every free run is greppable.
- [x] `engine/strategy.py` — the `Strategy` protocol: a pydantic
      `Params` model declaring each tunable with its type, range and default,
      plus `on_bar(ctx)`. The declared ranges are what Phase 5 sweeps.
      `swept()` writes the range into the field's own constraint *and* into its
      `json_schema_extra`, so the same numbers that reject an out-of-range
      parameter are the ones Phase 5 walks and Phase 7 renders — a range that
      lived in the launcher would be a second declaration that can disagree.
      `StrategyContext` is the entire surface a strategy has: `now`, `bars()`,
      `chain()`, `portfolio`, and two ways to order. No handle on the clock,
      the history or the broker, which is what makes the lookahead gate a short
      test. `ctx.chain()` is **declared and refused** — the signature belongs
      here because the two-clock design names it, the implementation belongs to
      Phase 10 alongside the pricing guardrail that stops it reading the
      synthetic source's meaningless boards.
- [x] `engine/history.py` — not in the original list, and where the lookahead
      guarantee actually lives. Bars from several symbols aligned onto one
      timeline that is the **union of the timestamps the bars have**, not a
      generated schedule: nothing is interpolated, and a session the lake is
      missing is a session the run does not visit. Immutable and stateless
      about *when* it is — every accessor takes the cursor and slices to it, so
      there is no method that returns a later bar. Opens are exact and marks
      are carried forward, and the asymmetry is the point: valuing a position
      at its last price is what a statement does, trading at one is a fill that
      never happened.
- [x] `engine/backtest.py` — the loop, whose whole content is the ordering:
      fill at the open, mark at the close, run the strategy, record the point.
      A run is a **value, not a side effect** — nothing is written anywhere,
      because the `run` and `trade` tables are Phase 5 and an engine that wrote
      to them could not be exercised without a database. Terminal positions are
      marked rather than liquidated, since a forced exit would penalise
      whatever was holding, and `buy_and_hold` — the baseline everything else
      is measured against — is the strategy that is always holding.
- [x] `strategies/` — **two shipped reference strategies**, which together are
      the proof-of-concept the vertical deploys with, plus an explicit
      `REGISTRY` rather than an import-order-dependent decorator:

      *`buy_and_hold`* — buy at the first bar, hold to the last. Four lines in
      `on_bar`, no parameters at all. It is the smallest honest example of the
      `Strategy` protocol, so it is the file to read before writing one, and
      Phase 6 needs it as a baseline regardless, so it costs nothing. It buys
      the *run's whole universe*, equally weighted, rather than taking a symbol
      as a parameter — which makes it parameter-free in fact rather than in
      claim, and makes it the correct baseline for Phase 6, where the baseline
      has to cover the identical window and universe as the result beside it.

      *`ma_crossover`* — a moving-average crossover with fast and slow windows
      declared as swept parameters. It exists because it is the only one of the
      two that can exercise a parameter schema, a sweep launcher and a
      multi-run leaderboard. A UI with one unparameterized strategy in it does
      not demonstrate the product. Long-only, and it orders **on the crossing
      rather than on the state**: a rule written as "if fast > slow, be long"
      rebalances every bar as equity drifts, which is hundreds of trades that
      are an artefact of how the rule was written and each of which pays costs.

      Neither is a candidate for making money, and the plan says so in the
      strategy's own description field so that the UI says so too — asserted in
      a test, because that field is the only place the disclaimer exists.
- [x] **What the reference strategies are expected to do, written down before
      they run — and corrected after running them.** The prediction was that
      against a zero-drift random walk, `ma_crossover` should **lose to
      `buy_and_hold` after costs**: it trades, trading costs money, and there is
      no signal to pay for it. The mechanism is right. **The measurement was
      wrong, and the gate as originally worded is not a fact about the engine.**

      The head-to-head difference on any one path is dominated by something far
      larger than costs: the crossover is long about half the time and the
      baseline is long all of it, so the gap between them is mostly the market's
      own realized move over the window — a coin flip with a standard deviation
      of tens of percent under a driftless walk. Measured over twenty seeds of
      the default universe across five years, the *gross* difference between
      them has a mean of about −7% against a standard error of about 8%, and
      the crossover wins outright on roughly half the individual paths. The sign
      also flips with the window at a fixed seed: on the default seed the
      crossover loses by 4.7% over 2021–2026 and *wins* by 4.9% over 2023–2026.
      A build gate asserting "the crossover loses" on one path asserts the sign
      of a coin flip and fails for reasons that have nothing to do with the
      engine.

      So the claim is decomposed into the two halves of it that are load-bearing
      and each is asserted in the form that is true:

      - **There is no gross edge to pay for.** Before costs, the crossover's
        advantage over the baseline is within three standard errors of zero
        across seeds — the same convention `test_synthetic_zero_alpha.py` uses.
        This is the half that breaks if the generator ever acquires structure.
      - **Costs are always paid, in the trader's direction.** On every seed,
        pricing the run leaves the crossover strictly further behind the
        baseline than running it free, by roughly half a percent of the account
        over five years, because it trades a hundred and sixty times to the
        baseline's five. This has no variance in it at all, and it is the half
        that breaks if the fill model, the slippage model or the commission
        model is wrong — which is what the gate was actually for.

      The shipped demo is asserted as itself on top of both: on `DEMO_WINDOW`
      (2021–2026, default seed, the configuration Phase 7 should seed from) the
      crossover does finish behind, so the seeded front page cannot quietly stop
      saying what the plan says it says.
- [x] **Determinism:** the same inputs and seed produce byte-identical results.
      Asserted in a test, because a non-reproducible backtest cannot be
      debugged and a comparison between two of them means nothing.
      `BacktestResult.canonical()` renders every number as a decimal string
      rather than a JSON float — a determinism check must not put its own exact
      accounting through a binary round trip on the way to being compared — and
      `fingerprint()` is the sha256 over it, which is what Phase 5's `run` row
      stores. Asserted in both directions: identical inputs agree, and a change
      to *any* input (a parameter, the cost model, the starting cash, the data)
      disagrees, or "identical" is being satisfied by a hash looking at nothing.
      A third test covers the quieter source of non-determinism — iteration
      order — by building one history from bars in two different orders.
- [x] A hand-checkable fixture: a tiny synthetic price series with known correct
      P&L, asserted to the cent. Every later engine change is measured against
      it. Four bars, two trades, and the arithmetic written out in the comments
      so it can be checked with a calculator and without reading the engine —
      which is the property that makes it worth more than its assertions, since
      a fixture whose expected values were computed could be silently rewritten
      to match a regression.
- [x] **Gate:** both reference strategies backtest over collected data · the
      hand-checked fixture passes to the cent · two runs of identical inputs are
      identical · a lookahead test fails the build if a strategy can see a bar
      it should not · the crossover has no gross edge over the baseline and
      pays strictly more in costs on every seed, with the seeded demo showing it
      behind — see the corrected prediction above.

      All five verified locally: `make trading-test` is green (ruff format,
      ruff check, pyright strict, 293 tests), the lake-backed gate writes
      Parquet through `LakeWriter` and reads it back through DuckDB rather than
      building bars by hand, and `ci.yml`'s `trading-boundary` guards still pass
      — one test input had to be reworded because the parent-traversal guard
      cannot tell a test's input from a real path dependency, and a guard that
      has to be taught exceptions is a guard someone turns off.
- [ ] **Commit:** "Trading: an engine, a fixture that proves it, and two strategies to run"

### [x] Phase 5 — Runs, sweeps, and the queue

**Ships:** launching a thousand parameter combinations and watching them land.
This is the ask's "lots of strategies, lots of parameters" made operational.

**Built 2026-09-02.** Every bullet below is committed and the gate passed in
full but for its fourth check, which needs the cluster and is marked with what
was measured in its place. Four things came out of building it and are recorded
in place rather than as a footnote: the phase needed a sixth table (below), the
queue needed a *second* mechanism beyond the visibility timeout the bullet names
(below), the demo needed a second sweep beside the grid (below), and the suite
needed a real Postgres for the first time in the silo's life (below).

- [x] Tables: `strategy`, `param_set`, `run`, `trade`, `run_metric`. A `run`
      records its strategy, its parameters, its data window, the
      `aerie-revision` that produced it, and the lake state it read.

      **Six tables, not five: `sweep` is the addition.** Cancellation is
      "cancel this sweep" rather than "cancel these thousand rows I hope I
      listed correctly", progress is a count against a denominator that has to
      exist before the runs finish, and Phase 6's selection accounting is
      explicit that *a run knows how many siblings its sweep produced* — which
      is a number about the batch and not about any run in it. A `sweep_id` on
      `run` with no table behind it would be a foreign key to a string somebody
      typed twice. It deliberately carries no `status` column: a sweep is
      finished when its runs are, and the one fact the runs cannot carry is
      that somebody asked it to stop, which is `cancelled_at`.

      *"The lake state it read"* is a sha256 over the bars the run actually
      read, not a partition list. A window and an interval cannot answer
      "were these two runs over the same data", because a partition
      re-collected after a provider correction covers the identical window with
      different numbers. Stored beside the result fingerprint, the pair makes a
      disagreement diagnosable rather than merely visible: same data and
      different results is a determinism bug, different data and different
      results is a lake that moved.

      `run_metric` is tall — `(run_id, name) → value` — because Phase 6 adds a
      walk-forward return, a deflated Sharpe and a re-score at every point of a
      cost-sensitivity sweep, and each of those is a migration if metrics are
      columns and an insert if they are rows. **An undefined metric is an
      absent row, never a NaN**: `NUMERIC` stores `NaN` happily and Postgres
      sorts it *above* every number, so one degenerate run would top a
      leaderboard sorted by risk-adjusted return.
- [x] A **Postgres work queue** — `SELECT … FOR UPDATE SKIP LOCKED`, retry
      counts, visibility timeouts. No Redis, no Celery, no new infrastructure,
      and queue depth becomes rows the control panel already reads.

      The last clause decided the shape: **the `run` table *is* the queue.** A
      `job` table beside it would make depth a join and would admit two states
      that cannot be represented at all this way — a job whose run was deleted,
      and a run with two jobs. The scheduling columns live on the row they
      schedule, behind two partial indexes so that a claim reads an index over
      the queued minority rather than over a table that grows forever.

      **The visibility timeout this bullet names is only half of the gate, and
      building it is what made that obvious.** A lease that expires is what
      stops work being *lost*. It does nothing about work being *duplicated* —
      because the worker declared dead may not be dead, but merely frozen, and
      about to wake up and write a result for a run somebody else has since
      completed. So every claim also mints a **fence token**, and a worker may
      only write while its token is still the one on the row. A reclaimed
      worker updates zero rows and discards its answer.

      Duplicated *execution* stays possible and is the price: two workers may
      compute the same backtest and exactly one may record it. That is the
      right side of the trade for a pure function of its inputs, and a backtest
      is one. The unique index on `(run_id, sequence)` in `trade` is the
      backstop under the fence — if the fence is ever wrong, a second blotter
      is a constraint violation rather than a doubled P&L.

      The reaper is a statement a worker runs **before each claim**, not a
      CronJob and not a second deployment. A worker is already connected and
      already about to ask the queue a question; a reaper is a deployment whose
      own death is silent; and the moment this most needs to run is the moment
      a worker died, which is exactly when its replacement is starting up.
- [x] A worker deployment, horizontally scalable, with a `PodDisruptionBudget`
      and a CPU limit — a sweep is deliberately unbounded compute, and the
      cluster runs a household. The limit is the point.

      [`deploy/cluster/trading/runs/`](../../deploy/cluster/trading/runs/), two
      replicas, reached as a base from the `trading` layer the way `../lake` is.
      **The CPU limit is the one deliberate break with this repository's
      convention**, which is memory limits and no CPU limits everywhere else,
      and the difference is the workload rather than a change of mind: every
      other pod here consumes CPU in proportion to work that arrives, and a
      backtest worker consumes exactly as much as it is given for as long as
      the queue is not empty. Without a limit, four replicas make the
      household's DNS, its Home Assistant and its photo library all mildly
      worse for an afternoon — which is worse than an outage, because nobody
      notices it.

      Three things follow from the workers being a Deployment rather than a Job
      per sweep, and each is a reason: parallelism becomes `kubectl scale`
      rather than a number fixed at creation, `replicas: 0` is a pause nothing
      else has to know about, and a pod that dies takes nothing with it because
      the queue already knows what is outstanding. The workers mount the lake
      **read-only** and run **no migration** — that is the control plane's init
      container, with one replica, for exactly this reason.
- [x] A sweep: pick a strategy, pick ranges over its declared parameters, get
      the cross product, enqueue N runs, watch them complete. Sweep size is
      estimated and confirmed **before** enqueueing.

      **The estimate is a handshake, not advice.** `plan_sweep` expands the
      grid; `enqueue_sweep` refuses unless the caller passes back the number
      the plan reported. A caller that never looked at the estimate cannot
      supply it, and one whose grid changed in between is told so instead of
      quietly launching the new one. An estimate that were merely *printed*
      would be satisfied by a launcher printing it into a log nobody reads,
      which is the version of this bullet that costs an afternoon of cluster
      CPU. `plan` and `enqueue` are therefore two commands rather than one with
      a flag, the refusal exits 2, and it happens **before a database
      connection is opened** — a correct refusal that first needs the database
      to be reachable is one an operator cannot get on the day the database is
      the problem. `TRADING_RUNS__MAX_SWEEP_RUNS` is the other half of the
      guard: the ceiling that does not depend on anyone reading the estimate.

      *"Ranges over its declared parameters"* is literal. `--sweep fast` walks
      the range the field itself declares via `swept()`, at the step it
      declares, so there is no number on the command line that can disagree
      with the model that validates it. Invalid corners are pruned at expansion
      through the strategy's own `Params` model — `ma_crossover`'s
      `fast >= slow` is rejected while the `param_set` is built rather than
      after a worker spent a minute producing it — and the difference between
      the cross product and the total is reported rather than hidden.
- [x] Metrics per run: total and annualized return, Sharpe, Sortino, max
      drawdown and its duration, exposure, turnover, win rate, trade count. All
      computed in one place; a metric defined twice will diverge.

      `engine/metrics.py`, one function, taking a `BacktestResult` and nothing
      else — which is what lets a worker write them in the same transaction
      that records the run, and what keeps the control plane from computing one
      a second way. Decimal throughout including the square roots, because a
      statistics layer that converted back to float would undo `engine/money.py`
      one ratio at a time. Every definition that has two conventions picks one
      and says why in place: the sample (`n − 1`) standard deviation, the
      full-sample downside deviation, a zero minimum acceptable return, and a
      turnover that is deliberately **not** annualized because its whole use is
      comparing two strategies over the same window.

      What is deliberately *not* here is anything needing a second run to
      interpret — a baseline, a walk-forward figure, a selection-adjusted
      Sharpe, a re-score at a higher cost assumption. All four are Phase 6, and
      all four are functions of several runs.
- [x] Cancellation, and resumption after a worker dies mid-run.

      Cancellation stops the *queued* runs immediately and lets the in-flight
      ones finish, which is a choice rather than a limitation. Killing them
      would need either a channel a worker polls mid-backtest — the engine loop
      has no callback for one and should not grow one for this — or rows left
      `running` with nobody holding them, waiting on a lease before anything
      reports the truth. In-flight work is bounded by the worker count, which
      is small and known. `cancelled_at` on the sweep is what makes a run that
      completes after the cancellation legible as exactly that.
- [x] **A named demo sweep**, defined here and enqueued by Phase 7's seed job:
      `ma_crossover` over a small grid of its two windows, on one synthetic
      symbol, over a fixed window. Small enough to finish on a cold cluster in
      minutes, large enough that the leaderboard has something to sort. Sizing
      it is a decision made once, in code, rather than a number an operator has
      to guess at first boot.

      **Two sweeps, not one.** The grid is four fast windows by five slow ones
      — twenty combinations, of which `fast = slow = 20` is pruned by the
      strategy's own validator, so nineteen runs — and beside it a single
      `buy_and_hold` run over the identical window, universe, cash and cost
      model. The plan names only the grid; the baseline is added because Phase
      6 requires *baselines computed over the identical window and shown next
      to every result*, and a seeded leaderboard holding nineteen crossovers
      with nothing to compare them against would demonstrate the machinery
      while withholding the one number that says whether any of it was worth
      doing — on the app's own front page, which is where this plan says the
      vertical must arrive demonstrating itself. It costs one run.

      It falls out of the sweep machinery rather than needing a second path:
      `buy_and_hold` has no swept parameters, so its grid is empty, and the
      cross product of no axes is exactly one run at every default.

      **The universe is the configured one, not a single symbol — a deviation
      from this bullet's wording, forced by measuring what the bullet
      produced.** Built as written, on `ZVZZT` alone over `DEMO_WINDOW`, the
      baseline falls 42% — one driftless five-year walk doing what a coin flip
      with a standard deviation of tens of percent does — and because the
      crossover is long only about half the time, **all nineteen of them beat
      it.** A seeded front page reporting nineteen out of nineteen strategies
      beating buy-and-hold, on data with no alpha in it by construction, is the
      exact false positive Phase 6 exists to keep from being mistaken for a
      finding, printed on the home page two phases before Phase 6 arrives.

      Over the configured universe the same grid scatters around the baseline,
      seven of nineteen ahead, with the baseline landing eighth of twenty. Nothing
      about the strategies changed: averaging five independent walks shrinks
      the *market's* own realized move without touching them. This is Phase 4's corrected prediction happening live —
      *"the head-to-head difference on any one path is dominated by something
      far larger than costs"* — and the response is the one that phase already
      chose, which is to stop reading a single path as a result.

      The sizing argument for one symbol survives the change intact, because
      the run count is what costs: nineteen runs plus a baseline either way,
      each a fraction of a second, over five symbols instead of one. Phase 4
      also calls `DEMO_WINDOW` *"the configuration Phase 7 should seed from"*,
      and the configuration it measures there is the default universe — so this
      is the reading that makes the two phases agree rather than a new
      preference.

      **The demo asserts nothing about who wins**, and that is the last
      deliberate part of it. Phase 4 retired exactly that claim — *"a build
      gate asserting 'the crossover loses' on one path asserts the sign of a
      coin flip"* — so what is asserted instead is the structural property that
      makes the page's comparison a comparison: the baseline covers the
      identical window, universe, cash and cost model as the grid beside it,
      and re-pointing the demo at a re-configured universe moves both or
      neither.
- [x] **Gate:** a 1,000-run sweep completes · killing a worker mid-sweep loses
      no runs and duplicates none · metrics for a hand-checked run match a
      hand-computed answer · the cluster stays responsive under a full sweep,
      measured rather than assumed.

      Three of the four are asserted in `make trading-test-db`; the fourth
      needs the cluster.

      **The first three needed a real Postgres, which is new for this silo.**
      Everything through Phase 4 was tested with no database anywhere — the
      `Database` and `RunLog` protocols exist so that the *unreachable* branch
      is reachable from a suite that has none. The queue cannot be tested that
      way and it is worth being precise about why: `FOR UPDATE SKIP LOCKED`
      deciding which of two concurrent workers gets a row, a conditional
      `UPDATE` matching zero rows, and a partial index over a predicate are all
      things the server does, and a Python reimplementation of them would pass
      while the SQL was wrong. So `ci.yml`'s trading lane gained a Postgres 18
      service container — the major CNPG runs — and the tests skip unless
      `TRADING_TEST_DATABASE_URL` names a scratch database. Opt-in rather than
      discovered, because they `TRUNCATE` what they connect to and a fixture
      that defaulted to `localhost:5432` would eventually find something that
      mattered. `make trading-test` stays green with nothing installed;
      `make trading-test-db` starts a throwaway server on 55432 and runs the
      lot.

      The 1,000-run sweep is 40 fast windows by 25 slow ones — a thousand
      rather than a thousand-and-something pruned to a number nobody chose —
      drained by two workers concurrently, because one worker would assert
      nothing about `SKIP LOCKED`. The worker-death gate is asserted at both
      levels: statement by statement, that a reclaimed run is claimable again
      and that its original holder's write lands nowhere; and end to end, that
      a worker which claims every run and then stops renewing leaves a sweep a
      successor finishes with exactly one blotter per run.

      A fourth thing was rehearsed while a live server was available and is
      worth naming because the offline check cannot do it: the migrations are
      applied forwards and rolled all the way back **against Postgres**, in a
      scratch schema. `tests/test_migrations.py` compares rendered DDL to the
      models and sends it nowhere, so a CHECK constraint calling a function
      that does not exist would have passed it and failed on the first deploy.

      **What was measured in place of the cluster check**, which needs one:
      the workers carry a 1-core limit each at two replicas, which is the
      bound the check is really about, and `/metrics` now carries
      `trading_runs{status=…}` beside an `up` gauge — so "the cluster stayed
      responsive under a full sweep" becomes a graph with the load on it rather
      than a recollection. Queue depth is derived at scrape time from the
      Ledger for the reason collection health is: a worker pod is gone by the
      time Prometheus arrives, so the durable row is the metric.
- [x] **Commit:** "Trading: sweeps, and a queue that survives a lost worker"

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
