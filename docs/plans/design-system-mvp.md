# Design system MVP — one vocabulary for admin and home

**Status:** Phases 0–4 done. Phases 0–5 are engineering and ship in order; **Phase 6
is a manual design pass with outside help** and is the gate everything after it
waits on. Phase 7 implements what Phase 6 decides — including the admin
navigation, which is deliberately *not* decided in this document.

This plan builds the vocabulary and the workbench. It does not decide what the
product looks like. That distinction is the whole shape of the plan: Phases 0–5
make every color, size and component a named thing in one place and put them all
on a page you can browse, so that Phase 6 is a designer changing values and
picking patterns rather than a designer rewriting apps.

## The ask

Verbatim, from the owner:

> Perform a major design pass over the admin and home/app-picker apps.
>
> Create a component library that they both reference for ui components so there
> is a shared look and feel. (In the long term we will expand this shared lib to
> all web apps but we're starting small with just these two.)
>
> Implement day/night mode so that the ui is dark at night.
>
> The admin app's primary nav pages are expanding to be too wide, explore another
> primary nav strategy.
>
> Make the Aerie Admin top bar much smaller. Improve the iconicity of the link to
> the home.landis.family app picker. We will want to reuse this same top bar for
> various apps around our stack as we extend this design pattern out. We do not
> need fluff like "System configuration and data management" in the title bar.
>
> Ultimately I like the design idea behind the dashboard web app. Incorporate as
> much of its philosophy as you can in your designs.
>
> Create a new top-level aerie app for the design component library. It has a
> primary nav of components, and I can see each component in various contexts and
> interact with it.
>
> Make commits along the way as you do chunks of work.

## Decisions made with the owner up front

| Question | Decision |
|---|---|
| How does day/night work in admin and home? | **OS preference plus a manual toggle.** `prefers-color-scheme` drives it by default; an Auto/Light/Dark control overrides and persists. The dashboard's circadian engine does **not** come along — see [Why the circadian engine stays on the wall](#why-the-circadian-engine-stays-on-the-wall) |
| How is the shared library wired in? | **npm workspaces.** A real workspace root at `src/Aerie.Web`, one hoisted lockfile, `@aerie/ui` as a workspace dependency. Blast radius is large and lands in Phase 0, alone, with no visual change |
| What replaces admin's 12-link nav? | **Deferred to the design pass (Phase 6).** Carried into the brief as its first open question, along with everything else in [What Phase 6 must account for](#what-phase-6-must-account-for) |
| What happens to the static app picker? | **It becomes a Vite React app** (`apps/home`) so it can consume the library like every other app |

## What is there today

### Six apps, six islands

`src/Aerie.Web/apps/{admin,auth,dashboard,modeler,docs,family}` are each a
standalone Vite app with **its own `package.json` and its own
`package-lock.json`**. There is no workspace root. Each one is built three
separate ways, and all three enumerate the apps by hand:

- **MSBuild** — one `Build<App>` target per app in
  [Aerie.Api.csproj](../../src/Aerie.Api/Aerie.Api.csproj), each shelling out to
  `npm install` + `npm run build` in that app's directory, guarded by a
  `Skip<App>Build` property.
- **Docker** — one `FROM node:22-alpine AS <app>-build` stage per app in
  [Dockerfile.api](../../src/Aerie.Api/Dockerfile.api), each with its own
  `npm ci` against its own lockfile, then a `COPY --from=<app>-build` into the
  SDK stage and a `-p:Skip<App>Build=true` on the publish line.
- **CI** — a matrix, `app: [admin, auth, dashboard, modeler, docs, family]`, in
  [ci.yml](../../.github/workflows/ci.yml), plus the same list spelled out again
  in the `test-web` target of the [Makefile](../../Makefile).

The cost of this is already on the floor: `lib/scale.ts` and
`lib/cameraStream.ts` exist in **both** admin and dashboard and have **drifted** —
same filename, different contents. There is no mechanism that would have caught
that, because there is no shared anything.

### The app picker is not an app

[`src/Aerie.Web/index.html`](../../src/Aerie.Web/index.html) is a single static
file with its CSS inlined in a `<style>` block, copied to `wwwroot/apps/` by the
`CopyAppsIndex` MSBuild target. It has no build step, so it cannot import a
React component, and it cannot bundle a font — which is why it carries a
hand-rolled `media="print"` + `onload` hedge to keep a third-party font request
off the critical path.

### Three apps, three font strategies, two icon strategies

| App | Font | Icons |
|---|---|---|
| dashboard | **Self-hosted**, `@fontsource-variable/manrope`, imported from `theme.css` | FontAwesome |
| admin | **Google Fonts CDN**, a render-blocking `<link>` in `index.html` | FontAwesome |
| home | **Google Fonts CDN**, with the `media="print"` hedge | Text glyphs (`▦`) |

Admin's is the worst of the three, and the comment in home's `index.html`
already explains why in this house's own words: a render-blocking subresource on
a third-party host means a WAN outage — LAN fine, house fine — holds first paint.
Converging on the dashboard's self-hosted face is a straight win and lands in
Phase 1.

### Admin's chrome

[App.tsx](../../src/Aerie.Web/apps/admin/src/App.tsx) renders a 32px-padded
gradient header with a 32px `<h1>`, the subtitle the owner wants gone, a `▦`
text glyph as the app-picker link, and **twelve flat `NavLink`s in a row**:
Zones, Routines, Panels, Calendars, Photos, Devices, Discovery, Settings,
Provisioning, People, Sessions, Revisions. That is the "too wide" in the ask,
and the list is still growing.

### The component inventory is already implicit

Counting `className` usage across admin's pages and components, the primitives
that exist as CSS conventions and want to become real components:

| Pattern | Uses | Becomes |
|---|---|---|
| `text-muted` / `text-danger` / `text-success` | 117 | `<Text tone>` |
| `btn-secondary` / `btn-primary` / `btn-danger` | 104 | `<Button variant>` |
| `field` + `field-label` | 105 | `<Field>` |
| `card` | 29 | `<Card>` |
| `admin-page-header` | 15 | `<PageHeader>` |
| `grid` + `cols-2` / `cols-3` | 24 | `<Grid cols>` |
| `admin-table` | 9 | `<Table>` |
| `badge` / `badge-muted` | 11 | `<Badge>` |
| `modal-overlay` / `modal-panel` / `modal-close` | 3 | `<Modal>` |

Nine primitives cover the overwhelming majority of admin's surface. That is the
Phase 4 scope, and it is small on purpose.

### What already exists and is good

Admin's [theme.css](../../src/Aerie.Web/apps/admin/src/theme.css) already has a
token vocabulary — `--bg`, `--card`, `--ink`, `--muted`, `--line`, `--primary`,
`--danger`, `--success`, `--shadow`, `--overlay`, `--transition` — **and the
static home page uses the same names**. It also carries a
`--series-1`…`--series-8` categorical ramp described as CVD-validated, and a
`prefers-color-scheme: dark` block. The starting point for `@aerie/ui`'s tokens
is therefore not a blank page; it is this vocabulary, extended with the scales
it is missing (radius, type, spacing) and given a manual override.

## Design commitments

The rules every phase is written against, and the review bar for any deviation.

1. **Phases 0–5 change no visual design that a designer has not chosen.** They
   move values into tokens, extract components, and rebuild plumbing. Where a
   value must be invented before Phase 6 (a radius, a nav layout), it is the
   *existing* value carried across, not a new one. This is what keeps Phase 6
   from being a rewrite.
2. **One vocabulary, stated once.** After Phase 1, a color, radius, font size,
   or spacing value written as a literal anywhere in admin or home is a bug.
   The token set lives in `packages/ui` and nowhere else.
3. **Renders-nothing discipline**, inherited from the dashboard: a component
   with nothing to say renders nothing. No empty tables with "no rows", no
   placeholder tiles. Empty states are a deliberate component, used deliberately.
4. **The top bar is a product, not a header.** It is built in `@aerie/ui` from
   the first line, with the assumption that auth, docs, modeler and family adopt
   it later. Nothing app-specific is compiled into it.
5. **Portable.** [ethos.md](../ethos.md) applies to the gallery's sample content
   as hard as to the apps: no real family names, place names, domains or
   addresses in any component demo. A `<Card>` example says "Living Room", not a
   real room in a real house, and never `landis.family`.
6. **Offline-first.** No render-blocking third-party subresource is added, and
   the two that exist today are removed in Phase 1. The house must draw its
   first frame with the WAN down.
7. **Both themes are authored.** Dark is not "light with the lightness flipped".
   Every token gets a considered value in both, and every phase's gate includes
   looking at it in both.

### Why the circadian engine stays on the wall

The dashboard's
[tokens.ts](../../src/Aerie.Web/apps/dashboard/src/theme/tokens.ts) /
[circadianTheme.ts](../../src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts)
is a 14-keyframe OKLCh timeline that derives ~30 CSS custom properties per
instant under contrast floors, with a veil-dip at the two polarity inversions.
It is genuinely good and it is genuinely not what admin needs, for three reasons
worth writing down so this is not relitigated:

- **It is a property of the room, not the user.** The wall tablet is in a known
  room whose light the sun controls. A laptop at 5pm is in an office with the
  blinds down or on a train. Golden-hour parchment is wrong there, and the
  operator has an OS-level preference that already encodes the truth.
- **It costs CSS.** The circadian palette is applied as inline styles on the
  root, because it changes continuously. A design system whose colors are not
  addressable from a stylesheet is a design system a designer cannot work in.
- **The keyframe table is load-bearing elsewhere.** It is read on the Kotlin
  side by
  [CircadianBrightness.kt](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/CircadianBrightness.kt).
  Making it serve a second, differently-shaped consumer is a way to break the
  wall's backlight.

What **does** come across from the dashboard is its philosophy, which was never
the sun: the four-step radius scale stated once and used everywhere, a named
type register instead of ad-hoc sizes, a single spacing base, calm (nothing
moves unless a person moved it), renders-nothing discipline, honest degradation,
and the explicit refusal of generic AI-dashboard ruts — KPI tile walls, gauge
clusters, glassmorphism. Those are Phase 1 and Phase 6 material respectively.

---

## Phases

### [x] Phase 0 — The workspace conversion

**Ships:** nothing visual. Every app builds, tests and deploys exactly as
before, from one lockfile. This is the phase that makes a shared package
possible at all, and it is deliberately alone so that if a build breaks, the
cause is unambiguous.

- [x] Add `src/Aerie.Web/package.json` with
      `"workspaces": ["apps/*", "packages/*"]` and the `engines.node` floor.
      (`vite-plugin-aerie-revision.mts` becomes a sibling of this file; `.mts`
      and `.mjs` are ESM by extension regardless of `"type"`, so its resolution
      is unaffected — worth a comment in the file so nobody adds `"type"` to
      fix a problem that does not exist.)
- [x] Delete the six per-app `package-lock.json`; generate one hoisted root
      lockfile. Verify no app silently lost or gained a transitive version.
- [x] **MSBuild** ([Aerie.Api.csproj](../../src/Aerie.Api/Aerie.Api.csproj)):
      one `NpmInstall` target at `../Aerie.Web` guarded on the root
      `node_modules`, which every `Build<App>` target depends on; each
      `Build<App>` becomes `npm run build -w apps/<app>` with
      `WorkingDirectory="../Aerie.Web"`. The `Inputs`/`Outputs` incremental
      globs stay per-app and gain `../Aerie.Web/packages/**` in Phase 1.
- [x] **Docker** ([Dockerfile.api](../../src/Aerie.Api/Dockerfile.api)):
      collapse the six `<app>-build` stages into one `web-build` stage — a
      single root `npm ci`, then every app's build, then one
      `COPY --from=web-build`. **This is a real cache regression to accept
      knowingly:** with a hoisted lockfile the six stages could no longer cache
      independently anyway (any app's dependency change rewrites the one
      lockfile and busts all six `npm ci` layers), so six stages would pay the
      install cost six times to buy nothing. One stage pays it once. Keep the
      existing `ARG`-after-install ordering so a new commit still restamps
      `index.html` without re-resolving the tree.
- [x] **CI** ([ci.yml](../../.github/workflows/ci.yml)): `cache-dependency-path`
      → the root lockfile; `npm ci` at the workspace root; lint/test/build via
      `-w apps/${{ matrix.app }}`. Keep the matrix — it costs a redundant
      install per job but buys per-app failure isolation in the PR checks, which
      is worth more than the minutes.
- [x] **Makefile** `test-web`: one root `npm ci`, then loop the apps with `-w`.
- [x] Check [.gitignore](../../.gitignore) for a `node_modules` rule that
      assumed the per-app layout, and [.gitattributes](../../.gitattributes) for
      a lockfile rule naming the old paths. (Note the known trap: this repo's
      `Backup*/` rule plus macOS case-insensitivity silently un-adds
      directories — run `git check-ignore` on anything new before trusting
      `git status`.)
- [x] **Gate:** `make test-web` green · `make build` green · `docker build -f
      src/Aerie.Api/Dockerfile.api .` green · the contents of
      `src/Aerie.Api/wwwroot/apps/` diff clean against a pre-conversion build,
      modulo content hashes.
- [x] **Commit:** "Web: six islands become one workspace"

### [x] Phase 1 — The token pass and day/night

**Ships:** admin goes dark at night, and every color/size in it comes from one
file. Composition is untouched — reviewable as a diff of numbers.

- [x] Create `src/Aerie.Web/packages/ui` — `@aerie/ui`, private, React as a peer
      dependency, `@fontsource-variable/manrope` as a real one. No build step:
      it ships `.tsx` and `.css` source and the consuming app's Vite bundles it.
- [x] `tokens.css` — the light palette on bare `:root`, extending admin's
      existing vocabulary with the scales it lacks:
      - **Radius**, four steps, the dashboard's shape: card / control / inset /
        chip, plus fully-round pills for badges. A radius stated as a number is
        a bug.
      - **Type register** — named steps, not sizes. Admin is a data-dense CRUD
        app read at desk distance; the dashboard's arm's-length register is
        *not* copied across, and Phase 6 owns the actual values.
      - **Spacing**, one base, one ladder.
      - **Color** — carry admin's `--bg`/`--card`/`--ink`/`--muted`/`--line`/
        `--primary`/`--danger`/`--success` forward unchanged, and carry
        `--series-1`…`--series-8` forward *exactly*: that ramp is
        CVD-validated and is not a Phase 1 value to improvise on.
- [x] Dark palette in **both** guards, so the toggle wins in both directions:
      `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) }`
      and `:root[data-theme="dark"]`. Seed from admin's existing dark block.
      No token may have its only definition inside a media query.
- [x] `useTheme()` + a tiny `<ThemeProvider>`: resolves `auto | light | dark`,
      writes `data-theme` on `<html>`, persists the choice in `localStorage`,
      and reacts to the OS preference changing while the tab is open. The
      *control* for it arrives in Phase 3 with the top bar; this phase ships the
      mechanism, so admin follows the OS immediately.
- [x] Self-host the font: `@fontsource-variable/manrope` imported from
      `tokens.css`, and **delete the Google Fonts `<link>` from
      `apps/admin/index.html`**. Add a `--ui` font token.
- [x] Point admin's `theme.css` at `@aerie/ui/tokens.css` and delete every
      declaration it now duplicates. Migrate the literals left in
      [App.css](../../src/Aerie.Web/apps/admin/src/App.css) onto tokens.
- [x] Add `../Aerie.Web/packages/**` to admin's MSBuild `Inputs` glob, or a
      library edit will not retrigger an incremental app build. **Every app that
      adopts the library needs this line** — it is the one piece of Phase 0's
      plumbing that does not generalize for free.
- [x] Two plumbing surprises the later phases will meet again:
      - [.gitignore](../../.gitignore)'s NuGet `**/[Pp]ackages/*` rule is a
        path glob, not a NuGet-aware one, and silently swallowed
        `src/Aerie.Web/packages/` - `git add` reported nothing and
        `git status` stayed clean. Negated explicitly, below the rule and above
        the `node_modules/` one so that ordering still holds.
      - [Dockerfile.api](../../src/Aerie.Api/Dockerfile.api)'s `web-build`
        stage copies each workspace's `package.json` ahead of `npm ci`;
        `packages/ui/package.json` is now one of them, because `npm ci` reads
        the lockfile's `link:` entry and fails outright if the target manifest
        is not on disk. **Phase 2 and Phase 5 each add a line here too.**
- [x] **Gate:** admin lints and builds · side-by-side screenshots show no
      intended visual change in light · dark mode is legible everywhere,
      including the chart tooltip, the modal overlay and every badge · the
      network tab shows no request to `fonts.googleapis.com`.
      *Result:* `make test-web`, `make build` and the Docker build are green;
      resolving both sides' tokens and diffing rule-by-rule leaves admin's
      light CSS byte-identical except the body's font stack gaining
      `'Manrope Variable'`, which is the one change the phase intends; the
      built bundle carries the five Manrope subsets and no reference to
      `fonts.googleapis.com`. Touching `packages/ui/src/tokens.css` retriggers
      an incremental `BuildAdmin` and an untouched tree still rebuilds nothing,
      so the new `Inputs` glob is doing its job in both directions.
- [x] **Commit:** "UI: one vocabulary, and a night for it"

### [x] Phase 2 — The design gallery app

**Ships:** `apps/design`, browsable, with a Tokens section. Small in surface and
disproportionately valuable: **this is the artifact Phase 6 is handed.** It
exists before the components so that every component built in Phase 4 is
developed inside it rather than inside a page of admin.

- [x] Scaffold `src/Aerie.Web/apps/design` matching the house pattern:
      `base: '/apps/design/'`, `outDir` into `wwwroot/apps/design`, the
      `aerieRevision` plugin, `.oxlintrc.json`, `.nvmrc`.
      (No per-app `.nvmrc`: Phase 0 hoisted that to the workspace root and no
      app has carried one since, so adding one back would be the odd file out.)
- [x] Primary nav down the left listing sections; a section per token group and
      later per component. The nav, the routes and the deep links all derive
      from one list in [`sections.ts`](../../src/Aerie.Web/apps/design/src/sections.ts),
      so Phase 3 and Phase 4 add a page with one entry.
- [x] A persistent **theme switch in the gallery chrome** — every page viewable
      in light and dark without leaving it. This is the single most useful thing
      the gallery does for Phase 6.
      Built as `<ThemeSwitch>` **in `@aerie/ui`**, not in the gallery: Phase 3
      needs exactly this control in the top bar, so building it in the app would
      have been work done twice. It is real radio inputs in a `<fieldset>`, so
      the group's keyboard and screen-reader contract is the browser's rather
      than hand-rolled ARIA.
- [x] Token pages: the color ramp with the token name and resolved value on each
      swatch, the type register as specimens, the spacing ladder, the radius
      steps, elevation, and the `--series-*` categorical ramp.
      Every value is **read back out of the cascade** with `getComputedStyle`
      rather than restated in the page — a swatch that restates its own hex is a
      swatch that can disagree with the token it claims to show, which is the
      one thing this app must not do. Each page also shows the context the token
      is chosen against rather than only the token: the accent colors under
      their `--on-accent` label, the two half-steps as three copies of the same
      list, the series ramp edge-to-edge as a timeline *and* at 2px as strokes.
- [x] Plumbing, all six places (this is the tax on a new app, and Phase 5 pays
      it again):
      `Build<App>` target + `Skip` property in the csproj · the `web-build`
      stage in the Dockerfile + its `-p:SkipDesignBuild=true` · `MapFallbackToFile`
      and an `AddRedirect` in [Program.cs](../../src/Aerie.Api/Program.cs) ·
      the `ci.yml` matrix · the Makefile `test-web` list · a tile on the app
      picker.
      Phase 1's warning held: the Dockerfile's `web-build` stage needed a
      seventh `COPY` of a workspace `package.json` before `npm ci`. **Phase 4
      added the eighth** — `packages/lib`, not an app — and Phase 5's app makes
      nine.
- [x] **Gate:** reachable at `/apps/design`, deep links survive a hard refresh,
      both themes correct, CI matrix green on the new entry.
      *Result:* `make test-web` green across all seven apps · `make build` green
      · `docker build -f src/Aerie.Api/Dockerfile.api .` green with the gallery
      in the image. Run from that image against a throwaway database,
      `/apps/design` 302s to the trailing slash, `/apps/design/type` and
      `/apps/design/series` both return the shell with a 200 (so a hard refresh
      on a deep link survives), the hashed assets and the favicon resolve, and
      the picker's new tile points at a live URL. Touching
      `packages/ui/src/tokens.css` retriggers an incremental `BuildDesign` and
      an untouched tree still rebuilds nothing, so the new `Inputs` glob works
      in both directions.
- [x] **Commit:** "Design: a room to see the parts in"

Two deliberate departures from the house pattern, both recorded in
[the app's README](../../src/Aerie.Web/apps/design/README.md):

- **No `clientLogger.ts`.** The gallery makes no API calls; it is a static
  bundle a designer browses with devtools open. The revision plugin's own header
  already calls the six copies of that module a problem, and a seventh would add
  to the duplication Phase 4 is chartered to reduce while buying nothing. Render
  errors go to the console through the app's `ErrorBoundary`.
- **The gallery states no component CSS.** It shows a component by rendering it.
  A gallery holding its own copy of a component's styles is a gallery that can
  drift from the component, which would make it worse than useless in Phase 6.

### [x] Phase 3 — The shared top bar

**Ships:** the ask's four top-bar demands, in a component built for reuse from
the first line. Admin adopts it; the gallery adopts it; home adopts it in
Phase 5.

- [x] `<TopBar>` in `@aerie/ui`: **much shorter** than today's 32px-padded
      gradient block, app name only — **the "System configuration and data
      management" subtitle is deleted, not shrunk.**
      The gradient, the ink on it and the `--measure` inner column are admin's
      own values carried across; the height is what changed. The app name drops
      from `--t-display` to `--t-section` because at 32px the name alone is two
      thirds of the new bar, and keeps the header's 800 weight.
- [x] An `<AppSwitcher>` as the leading element, replacing the `▦` text glyph.
      "Improve the iconicity" is a design judgement, so Phase 3 ships a real
      SVG mark with an accessible label and a hit target that reads as a
      control rather than a decoration — and **the mark itself is on Phase 6's
      list**. Same slot, same behaviour, better artwork later.
      The mark is the four-pane grid every platform uses for "all apps", drawn
      in `currentColor` so it needs no answer of its own for dark. It sits
      **alone in `AppsMark.tsx`** so Phase 6 replaces artwork in one file
      without touching the hit target, the hover state or the focus ring.
      A 36px target (from 40px, down with the bar), an `<a>` so middle-click
      and copy-link-address still work, and `aria-label="All apps"` where the
      glyph carried nothing an assistive technology could read.
- [x] The theme control from Phase 1 lands here, trailing.
      It needed a second ground: `<ThemeSwitch>` gained a `tone`, `surface` (a
      page or a card, Phase 2's appearance, unchanged) and `accent` (a filled
      bar). On the accent tone **every label stays at full `--on-accent` and
      the filled pill alone carries the state** — dimming the inactive two was
      the obvious separation and it puts 12px type at about 3.5:1 on the
      primary fill, under the 4.5:1 floor. Focus rings on the bar are drawn in
      `--on-accent` for the same reason: a `--primary` ring on the `--primary`
      fill is an invisible one.
- [x] Slots for app-supplied leading/trailing content, so a consuming app adds
      to the bar without forking it.
      `leading` renders after the app name, `trailing` before the theme
      control, which is always last so it is in the same place in every app.
      Under pressure the name is what gives way — it ellipses while the
      trailing group holds its size, because a target that shrinks to make room
      for a title is the wrong thing to shrink.
- [x] Admin renders it; its bespoke header CSS is deleted. The gallery renders
      it. A gallery page shows it in several contexts.
      The gallery's rail lost both its head and its foot to the bar: the app
      name was being stated twice and the theme switch now lives where every
      app keeps it. The new **Components** group in
      [`sections.ts`](../../src/Aerie.Web/apps/design/src/sections.ts) is the
      first entry in the group Phase 4 fills.
- [x] **Gate:** both apps build · the bar is materially shorter, measured ·
      keyboard reachable, labelled, both themes.
      *Result:* `make test-web` green across all seven apps · `make build`
      green. **Measured from the resolved CSS, 130.6px → 48px, 63% shorter:**
      the old header was 32px of padding twice around a 66.6px text block
      (a 32px title at `--lh-tight`, 4px, a 14px subtitle at `--lh-body`); the
      bar is a 48px `min-height` set by the 36px control inside it, and its
      only other occupant — the theme switch — stands 29.6px. No plumbing
      moved: the `packages/*/src/**` glob Phases 1 and 2 added already covers a
      new component file, and no new app or workspace package landed, so the
      Dockerfile, the CI matrix and the Makefile are untouched.
      Keyboard and labelling are verified by construction rather than by
      driving a browser: the switcher is an anchor with an `aria-label` and a
      visible `:focus-visible` ring, and the theme control is still the native
      `<fieldset>`/radio group Phase 2 built, so the browser supplies the whole
      arrow-key contract. Both themes are token-only — there is no literal
      color in any of the three new files. **The look in both themes is the
      owner's to confirm on screen.**
- [x] **Commit:** "UI: one bar, every app"

One thing this phase deliberately leaves broken-shaped, for Phase 4:

- **Admin has no `<h1>` now.** The app name in the bar is a wordmark, not a
  page heading, so it renders as a `<span>` and the `<header>` element carries
  identity as the banner landmark — otherwise the gallery, whose pages each own
  an `<h1>`, would have two. Admin's pages have always titled themselves with
  `<h2>` under the header's `<h1>`, so the top of admin's outline is now `h2`.
  Lifting those twelve headings is a visible type change (`--t-heading` →
  `--t-title`), which is exactly what this phase may not do on its own; it
  belongs with `<PageHeader>` in Phase 4, and is listed there.

### [x] Phase 4 — The primitives

**Ships:** the nine components from the inventory above, each with a gallery
page showing its states and contexts, and admin migrated onto them. Do this in
two or three commits by group rather than one — the migration touches every page
in admin.

- [x] Build in `@aerie/ui`, each landing with its gallery page in the same
      commit: `Button` · `Card` · `Field` · `Table` · `Badge` · `Modal` ·
      `PageHeader` · `Grid` · `Text`, plus `EmptyState` (new: commitment 3 needs
      a deliberate component to point at).
      Each carries admin's own values across unchanged. Where a component
      needed an answer admin had never written down, it is named and left plain
      rather than designed: `<Button loading>` looks exactly like `disabled`,
      and `<EmptyState>` renders admin's muted sentence and nothing else.
- [x] **`@aerie/ui/base.css`** — a second entry point beside `tokens.css`,
      holding the document layer both apps were stating twice: the box model,
      the body, the heading scale and the native form controls.
      It is not a convenience. `<Field>` renders a label around a control the
      *app* supplies — a raw `<input>`, `<select>`, `<textarea>` — so if the
      rules that make those look like Aerie's had stayed in admin's stylesheet,
      a `<Field>` specimen in the gallery would render a naked browser input.
      A gallery showing a component that looks different from how it looks in
      the app is worse than no gallery.
      Two rules are deliberately **not** in it, and each app states its own:
      `p { margin: 0 }` (admin's pages are laid out against the browser's
      paragraph margins; zeroing it here would move text on every page) and
      `html, body, #root { height: 100% }` (a full-height flex shell is an app
      decision, not a property of the vocabulary).
- [x] **`<PageHeader>` owns the page's `<h1>`.** Phase 3 moved the app name out
      of the heading outline and into the banner landmark, which leaves admin's
      pages titling themselves with `<h2>` and no `h1` above them. The lift is
      a visible type step, so it lands here with the component that makes it
      one edit rather than twelve.
      It carries a `level` prop, because a page has one `h1` and the Revisions
      page opens three sections under it — same shape, correct outline, rather
      than a second component that would have to be kept looking like this one.
- [x] Every gallery page shows **states, not just the happy one**: disabled,
      loading, error, long content, empty, and each variant in both themes.
      "See each component in various contexts and interact with it" is the ask;
      a static swatch grid does not satisfy it.
      The specimens are live: the buttons press, the loading row runs a real
      two-second save, the field's error appears when the value is genuinely
      out of range, and the modals take over the page and hand focus back.
      The gallery's two stand-ins — `.gallery-button` and `.stage-chip`, both
      written in Phase 2 with a note saying they existed only until the real
      thing landed — are deleted; the top-bar page now renders `<Button>` and
      `<Badge>`.
- [x] Migrate admin page by page. Each page's migration is mechanical — the
      components are extracted from admin's own CSS — and produces no visual
      change.
      **Three places where it was not, all corrections rather than design.**
      They are listed rather than absorbed, because commitment 1 says a visual
      change needs a reason on the page:
      1. **The `<h2>` → `<h1>` lift**, 22px → 28px on twelve pages. Planned
         above; the reason the phase owns `<PageHeader>` at all.
      2. **Two anchors that were asking to be buttons.** Calendars rendered
         `<a className="btn-primary">` and `<Link className="btn-secondary">`,
         and got only half the paint — `.btn-*` set a fill and an ink, while
         the padding, radius and weight came from the `button` element rule,
         which does not match an `<a>`. They were a coloured underlined link
         with no box. `<Button as>` renders them as the buttons the markup
         always meant, so the two remaining `btn-*` strings are gone.
      3. **Revisions' four page headers stack instead of splaying.** Their
         descriptions were `<p>` elements inside a `space-between` flex row, so
         each sentence sat *beside* its heading, pushed to the right edge. They
         are `description` now: under the title, muted, the house idiom.
      Two things the migration gained for free, neither of them visual:
      **fields are labelled** — admin's `<label className="field-label">` was
      associated with nothing, so clicking it did nothing and a screen reader
      read every input as unlabelled, and `<Field>`'s label wraps its control —
      and **the modal is a dialog**: `role="dialog"`, `aria-modal`,
      `aria-labelledby`, focus moved into it on open and returned to the opener
      on close. Before, a keyboard user who closed it tabbed the page behind
      the scrim. It is still not a full focus trap; that wants `<dialog>`'s
      top-layer behaviour or a tested library, and is Phase 7's to choose.
- [x] Resolve the drifted duplicates while in the neighbourhood: `lib/scale.ts`
      and `lib/cameraStream.ts` reconcile into `packages/` shared by admin and
      dashboard, or the divergence gets documented as deliberate. This is the
      concrete debt that motivated the workspace; leaving it is leaving the
      reason.
      *Reconciled,* into a new **`@aerie/lib`** — framework-free, per-module
      entry points, no barrel. The drift was hiding a hole in each copy:
      admin's `scale.ts` had grown `invertLinear` for its drag-to-select range
      and the dashboard's had `toPolylinePoints`, so each app was missing a
      function the other had written; the shared file is the union.
      `cameraStream.ts` was a verbatim copy whose header said so, and whose
      tests lived only in the dashboard — both apps now import the copy the 23
      tests actually cover.
      It is a package rather than a folder in `@aerie/ui` because nothing in it
      imports React and none of it is design: a camera's WebSocket protocol has
      no business in the design system.
      **The plumbing tax, for a package rather than an app:** the Dockerfile's
      `web-build` stage needed an eighth workspace `package.json` COPY before
      `npm ci` (Phase 2 predicted the eighth would be Phase 5's app; this one
      arrived first), the dashboard's `Inputs` glob in the csproj had to gain
      `packages/*/src/**` — it consumes a shared package now, and without that
      line a library edit would not retrigger its incremental build — and CI
      gained a **`web-packages`** job rather than a matrix leg, since the
      package has no build and every app leg would re-run its tests for
      nothing. The Makefile's `test-web` runs it once, before the app loop.
- [x] **Gate:** admin lints and builds · no `card`/`btn-*`/`field` literal
      class strings left in admin's pages · every gallery page correct in both
      themes.
      *Result:* `make test-web` green across all seven apps and the new
      package's 23 tests · `make build` green ·
      `docker build -f src/Aerie.Api/Dockerfile.api .` green, which is the
      check that mattered most here because the new workspace is exactly what
      `npm ci` fails on when a COPY is missing. Run from that image against a
      throwaway database, `/apps/design` 302s to the trailing slash,
      `/apps/design/button`, `/field` and `/empty-state` all return the shell
      with a 200 (so a hard refresh on a new deep link survives),
      `/apps/admin/zones` likewise, and both apps' hashed assets resolve.
      **The class-string gate is met:** `card`, `btn-primary`, `btn-secondary`,
      `btn-danger`, `field`, `field-label`, `badge`, `admin-table`,
      `admin-page-header`, `text-muted`, `text-danger`, `text-success`, `grid`,
      `cols-2`, `cols-3` and the three `modal-*` strings return nothing from a
      grep of `apps/admin/src`. Admin's `theme.css` lost 188 lines and its
      `App.css` 81; the net across the phase is 891 lines added, 1204 removed.
      Admin's remaining CSS is the handful of things only admin draws — a
      camera frame, an invite code, an album cover, a person's avatar — plus
      its nav strip and its margin utilities.
      **Both themes are token-only:** there is no literal color in any of the
      ten new component stylesheets, so a theme is correct here exactly when
      the palette is. **The look in both themes is the owner's to confirm on
      screen.**
- [x] **Commit(s):** "UI: the primitives move house" (× 2–3 by group)

Two things this phase leaves for later, both written down rather than left to
be discovered:

- **The heading outline skips `h2` inside a card.** Admin's cards title
  themselves with `<h3>`, which sat correctly under the old page-level `<h2>`
  and now sits under an `<h1>`. Lifting them is a second visible type step
  (`--t-subhead` → `--t-heading`), which is exactly what Phase 4 may not do on
  its own. It belongs with whatever Phase 7 decides about the type register.
- **`<Modal>` is not a focus trap.** Focus starts inside and returns to the
  opener; Tab can still leave for the page behind the scrim. Choosing between
  the native `<dialog>` element's top-layer behaviour and a tested library is a
  bigger decision than the primitives phase gets to make.

### [] Phase 5 — Home becomes an app

**Ships:** the app picker on the shared library. After this, the two apps in the
ask share a look and feel for real, and the plan's engineering half is done.

- [ ] Scaffold `apps/home` as a Vite React app consuming `@aerie/ui`, replacing
      the static `src/Aerie.Web/index.html` and its `CopyAppsIndex` target.
- [ ] **The output-path decision:** build to `wwwroot/apps/home/` with
      `base: '/apps/home/'`, like every other app, and redirect `^$` and
      `^apps/?$` to `/apps/home/`. The alternative — building to `wwwroot/apps/`
      itself to preserve `/apps/` as the picker's URL — requires
      `emptyOutDir: false`, because `emptyOutDir: true` on that directory would
      **wipe every sibling app's bundle**. Taking the redirect is cheaper than
      living next to that trap.
- [ ] Delete the `media="print"` font hedge: the app bundles its face now, which
      is what that comment said it wanted.
- [ ] Adopt `<TopBar>` — on home the app-switcher slot is the home state rather
      than a link away.
- [ ] The plumbing tax again, all six places, plus removing `CopyAppsIndex`.
      Note the two globs Phase 4 touched: the new app's `Inputs` needs
      `packages/*/src/**` from the first line if it consumes `@aerie/ui`, and
      the Dockerfile's workspace `COPY` list is now nine entries long.
- [ ] **Gate:** `/` and `/apps/` land on the picker · every tile still resolves ·
      both themes · offline-after-first-load still draws.
- [ ] **Commit:** "Home: the picker becomes an app"

---

## ⛔ Phase 6 — MANUAL: the design pass

> **STOP. This phase is not executed by Claude.**
>
> Everything above is scaffolding: a vocabulary, a workbench, and components
> wearing the clothes they already had. Nothing above chooses what Aerie looks
> like. **This is where you bring in outside design help.**
>
> Do not let Phase 7 start before this phase has produced answers. A design
> system that gets its values guessed at is a design system that gets rebuilt.

**What to hand them.** `/apps/design` is the deliverable — a live, browsable,
both-themes gallery of every token and every component in its states. Alongside
it: this document's [Design commitments](#design-commitments), the dashboard app
running at `?source=mock` as the reference for the philosophy the owner likes,
and [ethos.md](../ethos.md) for the portability constraint.

**What comes back.** Concrete enough to implement without further interpretation:

1. Values for every token in both themes — the palette, the type register, the
   spacing ladder, the radius steps, elevation.
2. A decision on the admin navigation (below).
3. Visual specifications for the top bar and the ten primitives, in their
   states.
4. The app-switcher mark.

### What Phase 6 must account for

The open questions, collected. This list is the brief.

1. **Admin's primary navigation — the deferred decision.** Twelve destinations
   today, growing; they no longer fit a row. The three patterns costed during
   planning were a **grouped left sidebar** (scales past twelve, group labels do
   explanatory work — "Discovery" next to "Devices" — full page height, drawer
   under ~900px), an **icon rail plus section sub-nav** (most compact, but two
   clicks to some pages and it needs an icon vocabulary for five sections), and
   a **compact top bar with dropdown groups** (smallest diff, keeps full-width
   content, but weakest at showing where you are). No recommendation is carried
   forward — this is the designer's call. Whatever is chosen, note that a
   grouping of the twelve is needed regardless, and that grouping is a product
   decision as much as a visual one.
2. **The top bar's final form.** Phase 3 makes it short and deletes the
   subtitle; it does not make it good. It must also work as the shared bar for
   auth, docs, modeler and family later — so anything that only makes sense for
   admin is wrong.
3. **The app-switcher mark.** "Improve the iconicity" is the ask and Phase 3
   only clears the ground for it. This mark ends up in the corner of every app
   in the stack; it is the highest-leverage single piece of artwork in the plan.
   The existing [aerie-logo.png](../../aerie-logo.png) and the
   [logo app](../../src/Aerie.Web/apps/logo/) are prior art. What ships today
   is a four-pane grid in
   [AppsMark.tsx](../../src/Aerie.Web/packages/ui/src/components/AppsMark.tsx),
   which is that file and nothing else — replacing the artwork touches no hit
   target, hover state or focus ring.
4. **How much dashboard philosophy translates.** The owner likes the wall's
   design idea. Some of it is portable — the stated radius scale, the named type
   register, calm, renders-nothing, the refusal of generic AI-dashboard ruts
   (KPI tile walls, gauge clusters, glassmorphism). Some of it is not: the wall
   is glanced at from across a room in portrait, and admin is a dense CRUD tool
   read at a desk in landscape. The wall's generous type and 26px card radius
   are answers to a question admin is not asking. Deciding which half comes
   across is the core aesthetic judgement of this phase.
5. **Dark is authored, not derived.** Both palettes are the designer's work.
   Admin's current dark block is a lightness inversion and it shows.
6. **Density.** Admin's tables and forms are its real surface. A type and
   spacing scale that flatters a card grid can make a twelve-column table
   unusable — the primitives must be specified against the dense case.
7. **The categorical ramp is constrained.** `--series-1`…`--series-8` is
   described as CVD-safe and is consumed by the state-timeline chart. A
   repalette either preserves that property or re-validates it; it does not
   quietly drop it. If charts are in scope at all, the house has a `dataviz`
   skill whose palette guidance should be reconciled with this ramp rather than
   competing with it.
8. **Contrast floors in both themes.** The dashboard's circadian system holds
   contrast floors by construction. This system will hold them by having been
   checked — so they need stating as numbers Phase 7 can assert against.
9. **Icons.** Admin and dashboard use FontAwesome; home uses text glyphs. One
   vocabulary, and a decision on whether FontAwesome stays (it is a dependency
   in every consuming app) or the system ships its own set.
10. **Portability.** Every example, mock and demo string in the gallery obeys
    [ethos.md](../ethos.md) — no real names, no real places, no
    `landis.family`. Aerie ships to other operators; the design system is not
    the place to leak this house.
11. **Offline-first is a constraint on aesthetics.** No design that requires a
    third-party font, icon CDN, or remote asset survives Phase 7. The house must
    draw with the WAN down.

---

### [] Phase 7 — Implement the design

Scope is defined by Phase 6's output, so the checklist below is the shape rather
than the content.

- [ ] Token values updated in `tokens.css`, both themes. Because Phases 1–5
      allow no literals, this step should be broad in effect and narrow in diff —
      that property is the whole return on the scaffolding.
- [ ] The navigation restructure in admin, per the decision. Expect this to be
      the largest single piece of work in the phase: it is a layout change to
      the app shell plus a grouping of twelve destinations.
- [ ] Top bar and app-switcher mark to spec; component visuals to spec, each
      verified on its gallery page in both themes.
- [ ] Contrast floors asserted, not eyeballed.
- [ ] **Gate:** owner eyeballs admin, home and the gallery in both themes, on a
      laptop and on a phone.
- [ ] **Commit(s):** by area, not one.

### [] Phase 8 — Dissipate

Per [the plans lifecycle](README.md#the-lifecycle), step 3 — the one that is
easy to skip and expensive to skip.

- [ ] Write `docs/design-system-architecture.md`: the token vocabulary and its
      rules, how an app adopts `@aerie/ui`, how the gallery is extended, the
      day/night contract, and the six-places plumbing checklist for a new app.
- [ ] Note the workspace layout in the root [README](../../README.md), which
      currently describes six independent apps.
- [ ] Delete this file and its row from [the plans table](README.md#current-plans).

## Verification

Beyond each phase's own gate:

- **`make test-web` and a Docker build after Phase 0**, before anything is built
  on the new foundation. A workspace conversion that breaks the release image is
  a conversion that gets discovered at deploy time.
- **Both themes at every gate.** Not "dark mode works" once in Phase 1 — every
  phase, every new surface.
- **The other four apps keep building.** auth, docs, modeler and family are not
  in this plan's scope, but Phase 0 rewires all six. Their green build is the
  assertion that the conversion was neutral.
- **UI and browser verification is the owner's.** Claude builds and lints;
  screenshots, feel, and the eyeball gates are yours.
