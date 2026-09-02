# Offsite chrome — the bar's job on hosts Aerie did not build

**Status:** Not started. Investigation complete; the four shaping decisions are
made (below) and every claim in [What each product actually allows](#what-each-product-actually-allows)
is verified against pinned source or vendor docs rather than assumed.

This plan puts a **way back to the app picker** — and as much Aerie identity as
each vendor supports — on `status.${DOMAIN}`, `metrics.${DOMAIN}` and
`logs.${DOMAIN}`. It deliberately does **not** put the real `<TopBar>` on them.
Why not is most of the document.

## The ask

Verbatim, from the owner:

> Investigate adding the global design header banner as a header in
> status.domain, metrics.domain, and logs.domain. I do not expect it is possible
> to totally whitelabel all of these products so let me know what options are
> available.

The expectation was right. None of the three can wear the bar. Two of the three
can carry a banner that does the bar's most useful job, natively and
upgrade-safely, and the third can be made to land on a page Aerie wrote.

## Decisions made with the owner up front

| Question | Decision |
|---|---|
| What is the header *for* on these hosts? | **A way back to the app picker.** Not pixel-identical chrome. This is what makes the whole plan fit inside supported vendor configuration |
| Is a proxy hop in front of the observability stack acceptable? | **No.** These are the tools you reach for when the house is broken; nothing new goes in their request path. This rules out HTML injection outright — see [The options that were ruled out](#the-options-that-were-ruled-out) |
| What does `status.${DOMAIN}` serve at its root? | **Investigate and recommend.** Recommendation: [switch it to a status page](#recommendation-switch-kumas-entry-page) |
| How far to go on Grafana, which has no supported route? | **Stay supported.** No patched assets, no replaced `index.html`. Grafana keeps looking like Grafana |

## What is there today

| Host | Product | Version | Routed by |
|---|---|---|---|
| `status.${DOMAIN}` | Uptime Kuma | `2.4.0-slim` | [`ingress-status.yaml`](../../deploy/cluster/observability/config/ingress-status.yaml) → `uptime-kuma:3001` |
| `metrics.${DOMAIN}` | Grafana OSS | via kube-prometheus-stack `88.3.0` | chart-managed, [`kube-prometheus-stack.yaml`](../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml) |
| `logs.${DOMAIN}` | OpenSearch Dashboards | `3.7.0` | [`ingress-logs.yaml`](../../deploy/cluster/observability/config/ingress-logs.yaml) → `opensearch-dashboards:5601` |

All three are third-party SPAs on their own subdomain, each keeping its own
login, each deliberately outside the auth wall —
[`auth-architecture.md`](../auth-architecture.md)'s allow-list table says so, and
[`apps.ts`](../../src/Aerie.Web/apps/home/src/apps.ts) repeats it where the
picker lists them. Each renders full-viewport chrome it owns completely.

### The bar already has an offsite form

The piece this plan would otherwise have had to invent already exists:
[`packages/ui/src/standalone/topbar.ts`](../../src/Aerie.Web/packages/ui/src/standalone/topbar.ts),
built by [`apps/chrome`](../../src/Aerie.Web/apps/chrome/) into a fixed-name
`topbar.js` + `topbar.css` pair that auto-mounts from `window.aerieTopBar`. It is
framework-free, imports the real `TopBar.css`, and exists precisely for "pages
Aerie did not build" — today Swagger UI and the logo export.

**It cannot be used here, and the reason is not technical shortcoming but
access.** Mounting it requires a `<script src>` in the host document. Swagger and
the logo page both let Aerie write into their `<head>`. None of these three does.
Getting a tag in there means rewriting the response on its way out, which is the
proxy hop the owner ruled out. The module stays what it is; this plan does not
touch it.

## What each product actually allows

### OpenSearch Dashboards 3.7 — the best of the three

Two separate supported mechanisms, both settable from chart values (the chart
exposes `config.opensearch_dashboards.yml`, verified against the published
values file):

- **`opensearchDashboards.branding`** — `logo.defaultUrl` / `logo.darkModeUrl`,
  `mark`, `loadingLogo`, `faviconUrl`, `applicationTitle`, `useExpandedHeader`.
  SVG/PNG/GIF by URL, with a real dark-mode variant. Officially documented. But
  the branded logo "functions as the home button" and its target is **not**
  configurable — so branding buys identity, not the way back.
- **`uiSettings.overrides['notifications:banner']`** — this is the way back.
  `notifications:banner` is a core ui setting of `type: 'markdown'`, verified in
  `src/core/server/ui_settings/settings/notifications.ts` at the 3.x tag,
  rendered as a banner across the top of every page. `uiSettings.overrides`
  accepts unknown keys (`schema.object({}, { unknowns: 'allow' })`) and *forces*
  the value, making it read-only in the UI — which is exactly what an
  IaC-declared value should be.

A markdown banner is not the Aerie bar. It is a strip at the top of every page
with a link in it, which is the job the owner asked the bar to do.

### Uptime Kuma 2.4 — nothing on the dashboard, a great deal on a status page

The operator dashboard — what `status.${DOMAIN}` serves today — has no branding
surface at all beyond a light/dark toggle.

Status pages have `title`, `description`, `footerText`, `customCSS`, an icon
upload, and `domainNameList`. Verified in `src/pages/StatusPage.vue` at the
`2.4.0` tag: `description` and `footerText` are both rendered as
`DOMPurify.sanitize(marked(...))` — so markdown *and* the HTML subset DOMPurify
keeps, which includes anchors, inline SVG and `class`. Paired with the
free-form `customCSS` field, that is enough to reproduce the bar's actual
appearance, not merely a link.

And the root is movable. `server/server.js` at the same tag reads
`Settings.get("entryPage")` and, when it starts with `statusPage-`, redirects
`/` to `/status/<slug>`. The operator console stays reachable at `/dashboard`.

### Grafana OSS 12 — nothing, and the owner has chosen to accept that

Custom branding — `menu_logo`, `app_title`, `login_logo`, `fav_icon`,
`footer_links` — is Enterprise and Cloud only. Grafana's own documentation states
it plainly: "Open Source Grafana does not include custom branding features."
Every OSS white-labelling guide in circulation patches `public/img/grafana_icon.svg`
or `public/views/index.html`, which a chart bump can silently revert.

What OSS *does* give, and what this plan uses: `default_theme`, and a
**provisioned home dashboard**. The `grafana_dashboard` sidecar is already
enabled and already watching all namespaces, so an Aerie-authored dashboard set
as the org home is one ConfigMap in a directory that already holds several. A
markdown text panel renders links without needing `disable_sanitize_html`, so
this stays inside the safe half of Grafana's config too.

Grafana's landing page becomes Aerie's. Every page after it is Grafana's. That
is the honest ceiling for OSS, and it is the outcome the owner selected.

## The options that were ruled out

Recorded because they are what someone will propose next, and because the
reasons are specific to this house rather than general.

- **A Traefik body-rewrite plugin** injecting `topbar.js` into every `text/html`
  response. One implementation covering all three plus every future service —
  genuinely the highest-reach option. Ruled out twice over. `experimental.plugins`
  makes Traefik fetch a Go module from `plugins.traefik.io` **at every pod
  start**: a WAN outage plus a Traefik restart takes the whole cluster's ingress
  down, which is the exact failure the design system's offline-first rule and the
  self-hosted Manrope decision were written against. `localPlugins` avoids the
  fetch but needs the plugin baked into the image, and Traefik here is the
  k3s-bundled chart patched by
  [`HelmChartConfig`](../../deploy/cluster/infrastructure/config/traefik-helmchartconfig.yaml) —
  a custom image would be fighting k3s for ownership of the object it patches.
  (Traefik `Middleware` CRs themselves are established practice here —
  [`middleware-auth.yaml`](../../charts/aerie/templates/middleware-auth.yaml) and
  [`middleware-kiosk.yaml`](../../charts/aerie/templates/middleware-kiosk.yaml) —
  it is the *plugin* that is the problem, not the middleware.)
- **An nginx `sub_filter` shim** per Service, doing the same injection with a
  pinned image and no fetch at startup. The honest version of the above, and it
  would work. Ruled out by the owner's second decision: it is a new component in
  the request path of the three tools you use when the house is broken.
- **An iframe shell** — an Aerie app at `status.${DOMAIN}` rendering the real
  `<TopBar>` around the product hosted on a second hostname. Pixel-perfect and
  cheap. Ruled out on merit: the address bar stops reflecting inner state, which
  destroys Grafana panel deep links and bookmarks — a large fraction of what
  Grafana is *for* — and each product's login gains a cross-site cookie problem
  it does not have today.

## The shape of the answer

Three hosts, three different mechanisms, one destination. Every entry point
below points at `https://home.${DOMAIN}/`, the picker's own host
([`ingress.yaml`](../../charts/aerie/templates/ingress.yaml)), spelled from the
`${DOMAIN}` Flux already substitutes into these manifests — so nothing here
hardcodes a domain, per [`ethos.md`](../ethos.md).

| Host | Mechanism | Result |
|---|---|---|
| `logs.` | `notifications:banner` + `branding` | A banner on every page, plus the Aerie mark, wordmark and favicon in OSD's own header |
| `status.` | `entryPage` → a provisioned status page with `description` + `customCSS` | The closest visual match to the bar of the three, on a page Aerie fully controls |
| `metrics.` | A provisioned home dashboard set as the org default | Grafana's landing page is Aerie's; the rest of Grafana is Grafana's |

### Recommendation: switch Kuma's entry page

Asked to investigate and recommend, the recommendation is **yes** — point
`entryPage` at a provisioned status page.

The argument for: the Kuma *dashboard* offers literally no surface to work with,
so leaving the root there means `status.${DOMAIN}` is the one host in this plan
that gets nothing at all. A status page gets `description` (sanitized markdown
and HTML), `footerText`, and arbitrary `customCSS` — enough to render something
that genuinely looks like the bar rather than merely links like it.

The argument against, taken seriously: a status page is unauthenticated, so this
publishes the house's service inventory and its up/down state to whoever can
reach the host. That is survivable **here specifically** because these hostnames
have no public A record —
[`reverse-proxy-architecture.md`](../reverse-proxy-architecture.md) resolves
certificates over DNS-01 for exactly this reason, and "the house's IP stays off
the public internet entirely." The audience for the status page is the LAN plus
the tailnet: the same people who could already reach Kuma's login screen and
learn the same thing from the page title. It is a change in convenience, not in
exposure class.

**This is the one decision in the plan that a different operator should be able
to decline.** Aerie ships to other operators, and one running these hostnames
with a public record has a materially different calculus. Phase 2 therefore
gates the entry-page switch behind its own flag rather than doing it
unconditionally, and the status page is created `published: false` first so the
switch and the exposure are two separate, separately reversible acts.

## Phases

### Phase 0 — Exempt the chrome assets from the auth wall

**This blocks Phases 1 and 2 and is the only code change in the plan.**

Every mechanism above wants to reference an Aerie-hosted asset — the mark for
OSD's `branding.logo`, and the bar's stylesheet for Kuma's `customCSS` — from a
foreign origin. `home.${DOMAIN}` is behind `aerie-auth`, and `/apps/chrome` is
**not** on the allow-list in
[`AuthGate.cs`](../../src/Aerie.Api/Services/Auth/AuthGate.cs). An `<img src>` or
`<link href>` from OSD or Kuma is challenged, 302s to the sign-in shell, and
renders as a broken image on a page whose whole point was to look deliberate.

- [ ] Add `/apps/chrome` to `AuthGate`'s prefix allow-list, beside `/apps/auth`.
      Justify it in the same voice as the entries around it: this is a static bar
      bundle and a wordmark, strictly less than `/apps/auth` — an unauthenticated
      sign-in shell — already hands out.
- [ ] Confirm the bundle carries nothing else. The build emits `topbar.js`,
      `topbar.css`, `swagger.css` and hashed font files; a prefix exemption opens
      whatever is mounted under it later, so the note has to say that out loud.
- [ ] Add an `AuthGate` test asserting `/apps/chrome/topbar.css` is exempt and
      `/apps/chromefoo` is not — the `StartsWithSegments` hazard the file's own
      comment at line 93 already calls out.

### Phase 1 — `logs.${DOMAIN}`

- [ ] Add `config.opensearch_dashboards.yml` to the `opensearch-dashboards`
      HelmRelease in [`opensearch.yaml`](../../deploy/cluster/observability/controllers/opensearch.yaml)
      with `opensearchDashboards.branding`: `applicationTitle`, `faviconUrl`,
      `logo`/`mark` with both `defaultUrl` and `darkModeUrl`.
- [ ] Set `uiSettings.overrides['notifications:banner']` to a markdown line
      linking to `https://home.${DOMAIN}/`. Keep it to one line: it is on every
      page forever, not a notice.
- [ ] Verify the `${DOMAIN}` substitution survives the render. The file's
      neighbours already carry a warning about this —
      [`kuma-provision.yaml`](../../deploy/cluster/observability/config/provisioning/kuma-provision.yaml)
      documents kustomize stripping quotes and Flux then substituting an integer
      into a string field. A URL is not a number and is safe by shape, but the
      banner is the first *free text* value substituted in this tree.
- [ ] Confirm the branding assets resolve cross-origin after Phase 0, in both
      themes, with a cold cache.

### Phase 2 — `status.${DOMAIN}`

[`provision.py`](../../containers/kuma-provision/provision.py) already logs in
via `uptime_kuma_api` and drops to `api._call(...)` for raw socket.io methods
(`needSetup`, `setup`), so `addStatusPage` / `saveStatusPage` / `setSettings` are
reachable the same way. It runs hourly and is written to converge, which is what
makes a status page's contents declarable rather than clicked.

- [ ] Add an idempotent `ensure_status_page` pass: create the slug if absent,
      then save `title`, `description`, `customCSS` and `footerText` on every run
      so an edit in the UI converges back.
- [ ] Author the `description` as the bar: an `<a>` to `https://home.${DOMAIN}/`
      wrapping the four-pane apps mark as inline SVG, using the same class names
      `standalone/topbar.ts` uses. Confirm DOMPurify's default policy keeps the
      SVG and the `class` attribute — assert it in a test rather than trusting it.
- [ ] Derive `customCSS` from the bar's real stylesheets rather than retyping
      them. If that cannot be done without a build step in the provisioning
      image, restate it and say so in a comment — the rule
      `standalone/topbar.ts` already sets is *never invent a class name*.
- [ ] Create the page `published: false`. Leave it there.
- [ ] Add a chart value gating the `entryPage` switch, defaulting to the current
      behaviour. Flipping it is a second, separate act.
- [ ] Only then: set `entryPage` to `statusPage-<slug>` and publish. Verify
      `/dashboard` still reaches the operator console and that the status page
      links to it.

### Phase 3 — `metrics.${DOMAIN}`

- [ ] Add an Aerie home dashboard as a `grafana_dashboard`-labelled ConfigMap
      beside the existing ones in
      [`config/dashboards/`](../../deploy/cluster/observability/config/dashboards/).
      A markdown text panel with the way back and links to the two sibling tools —
      no `disable_sanitize_html`, which keeps this inside supported territory.
- [ ] Point `grafana.ini`'s default home dashboard at it, in the same values
      block that already sets `root_url`.
- [ ] Set `default_theme` to match the house's default.
- [ ] Verify a kube-prometheus-stack upgrade leaves all three intact — the whole
      point of the "stay supported" decision is that this step is boring.

### Phase 4 — Close out

- [ ] Fold the durable half into
      [`monitoring-alerting-architecture.md`](../monitoring-alerting-architecture.md):
      the three mechanisms, and the fact that a fourth observability tool gets a
      way back by the same route rather than by a new one.
- [ ] Record the auth-wall exemption in
      [`auth-architecture.md`](../auth-architecture.md)'s allow-list section.
- [ ] Delete this file. A finished plan is not an archive.

## What this deliberately does not do

- **It does not put `<TopBar>` on these hosts.** Three different mechanisms
  produce three things that do not look alike. That is the true cost of the "no
  proxy hop" decision, and it is the right trade — but nobody should later read
  this plan as having achieved a unified bar.
- **It does not white-label Grafana.** Not by patched assets, not by a mounted
  `index.html`. If that ever becomes worth it, the honest options are Grafana
  Enterprise or the nginx shim, and both should be reopened as decisions rather
  than smuggled in as a fix.
- **It does not put these hosts behind the auth wall.**
  [`auth-architecture.md`](../auth-architecture.md) already tracks that as
  deferred work — one annotation each plus proxy-auth config per tool. It is a
  different change with a different risk profile, and doing it here would hide it.
- **It does not carry theme across origins.** `themeStore` is `localStorage`,
  which is per-origin, so a theme chosen in admin cannot follow the operator to
  `logs.${DOMAIN}`. Each product keeps its own theme setting. Nothing short of
  a shared cookie on the parent domain changes that, and that is not worth it for
  three pages.
