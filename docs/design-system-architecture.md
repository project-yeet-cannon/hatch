# Design System Architecture

## Summary

Aerie's web apps share one vocabulary and one set of components, both living in
**`@aerie/ui`** — a package inside the `src/Aerie.Web` npm workspace, consumed
by every app that renders house chrome.

The design goal is the same shape as the one behind
[the family apps](family-apps-architecture.md): a *property*, not a style guide.
The property is that **a color, radius, type step or spacing value written as a
literal in an app is a bug** — there is one place those values live, a designer
can change them there, and every app changes with them. Nothing enforces that
mechanically. What makes it hold is that the alternative is genuinely more work:
the token is already named, already has an answer in both themes, and is already
on a page you can look at.

Three things follow from it and are the rest of this document:

- **The vocabulary** — what is a token, what is not, and why the list is closed.
- **The day/night contract** — one stored choice, honoured by React apps and by
  the two pages that are not apps, resolved with no JavaScript in the common
  case.
- **The gallery** — `apps/design`, where every token and component is rendered
  in both themes and in the states an app never shows you.

The mechanics of each consuming app live next to that app, in its own README.
What is here is the part no single app owns.

**What this document does not do is decide what Aerie looks like.** Every value
in `tokens.css` is admin's existing value carried across and given a name. The
four-step radius scale, the named type registers and the single spacing base are
the dashboard's *philosophy*; the numbers in them are placeholders with a home.
A design pass moves the numbers, and the whole point of the structure below is
that moving them is an edit to one file rather than a rewrite of eight apps.

## The workspace

`src/Aerie.Web` is a single npm workspace: `apps/*` and `packages/*`, one
hoisted `package-lock.json` at the root, one `node_modules`.

```
src/Aerie.Web/
  package.json          workspaces: ["apps/*", "packages/*"]
  package-lock.json     one lockfile, hoisted
  apps/
    admin  auth  chrome  dashboard  design  docs
    family  hatch  home  logo  modeler  trading
  packages/
    ui                  @aerie/ui   — the vocabulary and the components
    lib                 @aerie/lib  — the logic two apps share
```

It was six standalone apps with six lockfiles, and the cost of that was already
on the floor: `lib/scale.ts` and `lib/cameraStream.ts` existed in both `admin`
and `dashboard` and had **drifted** — same filename, different contents, and
nothing in any build that would ever have said so. Both now live in
`@aerie/lib`, which is the concrete debt the conversion was for.

### The two packages, and the line between them

| | `@aerie/ui` | `@aerie/lib` |
|---|---|---|
| What | Tokens, the document layer, React components | Framework-free logic |
| Imports React | Yes, as a peer dependency | **Never** |
| Entry points | A barrel (`.`) plus `./tokens.css`, `./base.css`, `./standalone/topbar` | One per module: `./scale`, `./cameraStream` |
| Build step | None | None |

Neither package builds. Both ship `.ts`/`.tsx`/`.css` source and the consuming
app's Vite bundles it, so a token edit is a one-file edit with no publish or
compile round-trip — and in `npm run dev` it hot-reloads into whatever app is
open. That is what makes the gallery a workbench rather than a report.

The entry-point difference is deliberate. `@aerie/ui` exports a barrel because
each component imports its own colocated stylesheet, and a CSS import is a side
effect the consuming app wants in its bundle anyway; both consumers render most
of the library, so per-component entry points would buy nothing and cost every
import site an extra specifier. `@aerie/lib` has no such reason, and a barrel
there would put the camera protocol into the bundle of an app that only wanted
to draw an axis.

A module earns its way into `@aerie/lib` by already existing in two apps and
having started to drift. It is not a place to put things that might be shared.

### Who consumes what

| App | `@aerie/ui` | Note |
|---|---|---|
| `admin` | yes | Also `@aerie/lib`. The app the primitives were extracted from |
| `home` | yes | The app picker |
| `design` | yes | The gallery |
| `docs`, `modeler`, `hatch`, `trading` | yes | Adopted the bar and the tokens |
| `chrome` | yes | Not an app — the build that emits the standalone bar |
| `dashboard` | **no** | Also `@aerie/lib`. Its palette is the circadian engine — see below |
| `auth`, `family` | not yet | Carry their own palettes, in the same token *names* |
| `logo` | n/a | A static export; it wears the standalone bar |

`auth` and `family` are the honest state of things rather than an oversight:
they use the same token names (`--bg`, `--card`, `--ink`, `--muted`, `--line`,
`--primary`…) with their own values, so adopting `@aerie/ui` is a stylesheet
swap rather than a rewrite. `family`'s palette is sky and slate rather than
admin's neutral grey because it is the family-facing side of the product; which
of the two is right is a design decision, and until it is made, two palettes
under one set of names is better than one palette imposed by whoever edited
last.

## The vocabulary

[`packages/ui/src/tokens.css`](../src/Aerie.Web/packages/ui/src/tokens.css)
is the whole vocabulary. It states values and paints nothing.

### The rules

1. **A literal is a bug.** A color, radius, type step or spacing value written
   anywhere but `tokens.css` is a value a designer cannot find, cannot change,
   and that has no answer for the other theme.
2. **The list is closed.** A size outside the type table, a radius stated as a
   number, an eleventh spacing rung — each of those is a decision, and the way
   to make it is to add a token, not to write the number.
3. **No token may have its only definition inside a media query.** Every token
   is defined on bare `:root`; the dark palette *overrides*. A token that
   exists only under `prefers-color-scheme` is a token that vanishes in the
   other theme, and the failure is silent.
4. **Both themes are authored.** Dark is not light with the lightness flipped.
   Every value has a considered answer in both, and every review looks at both.
5. **The font is imported by the tokens**, not by an app entry point, so an app
   cannot adopt the vocabulary and forget the face it was drawn for.

### The registers

| Group | Tokens | The rule it carries |
|---|---|---|
| Font | `--ui`, `--mono` | Manrope Variable, self-hosted via `@fontsource-variable/manrope`. One request, ~25KB, `font-display: swap` |
| Radius | `--r` 8 · `--r-ctl` 6 · `--r-in` 4 · `--r-chip` 2 · `--r-pill` 999 | Card / control / nested block / chip. A circle is `50%`, which is a shape and not a scale step |
| Type | `--t-display` 32 · `--t-title` 28 · `--t-heading` 22 · `--t-subhead` 18 · `--t-section` 16 · `--t-meta` 15 · `--t-body` 14 · `--t-label` 12 · `--t-micro` 11, plus `--lh-tight` / `--lh-body` | Named registers, not sizes. `base.css` binds `h1`–`h6` to them, so a page states its rank with the right element and gets the right size |
| Spacing | `--sp-1` 4 → `--sp-10` 40, with `--sp-1-5` 6 and `--sp-2-5` 10 | Base 4. The two half-steps are named rather than hidden as literals so a design pass can see them and decide whether to keep them |
| Measure | `--measure` 1200px | Every page's content column is capped here |
| Ground | `--bg`, `--card`, `--ink`, `--muted`, `--line`, `--line-strong` | `--line-strong` is a line that has to carry weight: a pressed secondary button, a divider read against `--card` rather than `--bg` |
| Accents | `--primary`, `--danger`, `--success`, `--warn`, each with a `-bg` wash and a `-ink` | Four meanings, not four colors. `--warn` is amber rather than orange so it is not mistaken for `--danger` at a glance down a column |
| On-accent | `--on-accent`, `--on-accent-wash` | Ink laid *on* an accent fill. It flips with the theme: the dark accents are light blues and corals, and white on them is a ~2.2:1 label nobody can read |
| Series | `--series-1` … `--series-8` | The categorical ramp, fixed order, CVD-validated. Not a ramp to improvise on |
| Letterbox | `--letterbox` | Black in both themes — it is the absence of picture, not a surface |
| Motion & elevation | `--transition`, `--shadow`, `--overlay` | Calm: nothing moves unless a person moved it |
| Scheme | `color-scheme` | Set alongside the palette in every guard. This is what keeps a native `<select>` popup from arriving white in a dark app |

### What is deliberately not a token

- **The fixed dimensions of an ornament.** A 40px avatar, a 64×48 album cover.
  They are sized to their content, and a designer retuning the spacing ladder
  must not resize them by accident. The ladder is for space *between* things.
- **Hairlines and 1–3px optical nudges.** Below the base, they are free.
- **A glyph scaled to fit a fixed box.** That is an ornament, not text, and the
  type table does not govern it.

## Day and night

### The contract

One stored choice — `auto | light | dark` — under one key, `aerie.theme`,
namespaced because every app shares an origin.

```
choice          <html data-theme>     what governs
--------------  --------------------  ------------------------------------
auto            (attribute absent)    prefers-color-scheme, in CSS
light           data-theme="light"    the bare :root palette
dark            data-theme="dark"     the [data-theme="dark"] palette
```

The dark palette is stated **twice, identically**: once under
`@media (prefers-color-scheme: dark)` guarded by `:root:not([data-theme='light'])`,
and once under `:root[data-theme='dark']`. That is what makes the toggle win in
*both* directions — an operator who prefers dark at the OS level and wants this
one app light gets it, and so does the reverse. The two blocks are the same
palette and have to be kept in sync.

`auto` **removes** the attribute rather than writing out the resolved value.
With no attribute, the media guard governs, so a machine that changes theme
while the tab is backgrounded is already correct on the next paint with no
JavaScript involved.

### The mechanism, and why it is in two files

[`theme/themeStore.ts`](../src/Aerie.Web/packages/ui/src/theme/themeStore.ts) is
the whole mechanism — read, write, resolve, apply, watch — with **no React in
it**. [`theme/ThemeProvider.tsx`](../src/Aerie.Web/packages/ui/src/theme/ThemeProvider.tsx)
is only the React around it: the state components read, the subscription, the
context.

The split exists because there are two callers. The other is
[`standalone/topbar.ts`](../src/Aerie.Web/packages/ui/src/standalone/topbar.ts),
the bar on pages Aerie did not build with Vite. Those pages must read and write
the *same* stored choice as the apps — a house where the theme you picked in
admin does not survive the click into Swagger is a house with two themes — and
importing `ThemeProvider` to get it would drag React onto a static page to run
twelve lines of `localStorage`.

Three behaviours worth knowing before touching it:

- **`localStorage` throws** rather than returning null in a partitioned or
  locked-down context. An unreadable store means `auto`; an unwritable one means
  the choice holds for the tab and is forgotten on reload. Neither is an error
  worth surfacing — refusing to change theme at all is worse.
- **The attribute is written in `useLayoutEffect`**, not `useEffect`, so a
  stored `dark` on a light-preference machine never shows a light frame first.
- **`useTheme()` throws outside a `<ThemeProvider>`.** A silently-light toggle
  is harder to find than a stack trace, and `<TopBar>` always renders the switch
  — so a provider above it is a requirement, not a nicety.

### Why the wall's circadian engine is not this

The dashboard's [`circadianTheme.ts`](../src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts)
is a 14-keyframe OKLCh timeline deriving ~30 custom properties per instant under
contrast floors. It is good and it is not what a design system needs, for three
reasons worth writing down so this is not relitigated:

- **It is a property of the room, not the user.** The wall tablet is in a known
  room whose light the sun controls. A laptop at 5pm is in an office with the
  blinds down, or on a train. Golden-hour parchment is wrong there, and the OS
  preference already encodes the truth.
- **It costs CSS.** A continuously-changing palette is applied as inline styles
  on the root. A design system whose colors are not addressable from a
  stylesheet is one a designer cannot work in.
- **The keyframe table is load-bearing elsewhere.** It is read on the Kotlin
  side by `CircadianBrightness.kt` ([kiosk-architecture.md](kiosk-architecture.md)).
  Making it serve a second, differently-shaped consumer is a way to break the
  wall's backlight.

So the dashboard keeps the sun, takes `@aerie/lib` and not `@aerie/ui`, and what
comes across from it into the design system is its *philosophy*: one radius
scale stated once, named type registers, a single spacing base, renders-nothing
discipline, and calm.

## Adopting `@aerie/ui`

### The wiring

An app's own stylesheet takes the vocabulary and the document layer:

```css
/* apps/<app>/src/theme.css */
@import '@aerie/ui/tokens.css';
@import '@aerie/ui/base.css';
```

and the entry point provides the theme and the bar:

```tsx
/* apps/<app>/src/main.tsx */
import { ThemeProvider } from '@aerie/ui';
import './theme.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <ThemeProvider>
        <BrowserRouter basename="/apps/<app>">
          <App />
        </BrowserRouter>
      </ThemeProvider>
    </ErrorBoundary>
  </StrictMode>,
);
```

```tsx
/* apps/<app>/src/App.tsx */
import { TopBar } from '@aerie/ui';
<TopBar appName="Aerie <App>" />
```

Add `"@aerie/ui": "*"` to the app's `dependencies` — a workspace link, so
editing a token hot-reloads with no publish step — and add
`../Aerie.Web/packages/*/src/**/*.*;../Aerie.Web/packages/*/package.json` to the
app's MSBuild `Inputs` glob. Without that last line a token edit will not
retrigger the app's incremental build, and the app will ship a stale palette
from a build that looked successful.

### Two entry points, because they are two kinds of thing

`tokens.css` states values and paints nothing. `base.css` paints: the box model,
the body's face and ground, the heading scale bound to the type registers, and
the native form controls.

The native controls are why `base.css` exists at all rather than being a
convenience. `<Field>` renders a label around a control the *app* supplies — a
raw `<input>`, `<select>`, `<textarea>`. If the rules that make those look like
Aerie's had stayed in admin's stylesheet, a `<Field>` specimen in the gallery
would render a naked browser input, and a gallery that shows a component looking
different from how it looks in the app is worse than no gallery.

Two rules are deliberately **not** in `base.css`, and each app states its own:

- **`p { margin: 0 }`.** Admin's pages are laid out against the browser's
  default paragraph margins and its own utilities stack on top of them. The
  gallery makes the opposite call. Zeroing it centrally would move text on every
  admin page.
- **`html, body, #root { height: 100% }`.** A full-height flex shell is an
  app-shell decision, not a property of the vocabulary.

### The components

| | |
|---|---|
| Chrome | `<TopBar>`, `<AppSwitcher>`, `<ThemeSwitch>` |
| Frame | `<PageHeader>`, `<Card>`, `<Grid>`, `<Table>`, `<Modal>` |
| Controls | `<Field>`, `<Button>` |
| Text | `<Badge>`, `<Text>` |
| The exception | `<EmptyState>` |

The nine primitives after the chrome are the nine patterns that already existed
as CSS conventions in admin — the `text-*` tones at 117 sites, `btn-*` at 104,
`field` at 105 — extracted rather than invented. That is why the set is small
and why it is the right small set.

Four conventions run through them:

- **Polymorphic where the tone belongs on an existing element.** `<Text as="td">`
  colors a cell rather than wrapping its contents in a span with an opinion.
  `<Button as={Link}>` navigates, and middle-click and open-in-new-tab come from
  the element rather than from the paint.
- **Variant names say what the phrase *means*, not how loud it is** — `muted`,
  `danger`, `success` — and the resting variant is the default because it is the
  default in practice.
- **Renders-nothing discipline.** A component with nothing to say renders
  nothing: no empty table with a "no rows" row, no reserved slot for an absent
  description. `<EmptyState>` is the named exception, and the fact that you have
  to import it is the point — an empty state you import is an empty state
  somebody decided on.
- **A component restates its own appearance** rather than leaning on `base.css`,
  so it renders correctly in an app that took the tokens and not the document
  layer.

`<TopBar>` deserves its own note. It is 48px — one row, the height of the
control in it — replacing about 131px of gradient header, and it is **not a page
heading**: the app name is a wordmark in a `<span>`, so the `<h1>` stays with the
page's own heading, which is what `<PageHeader>` owns. An app that needs more in
the bar passes `leading`/`trailing`. Nothing app-specific compiles into it, and
extending it by forking it is the failure this package exists to prevent.

`<Modal>` has one slot worth naming. Its panel caps at the viewport and scrolls,
which is right until the dialog holds a row that has to stay reachable — its
actions. Passing `footer` moves that row out of the scroll: the panel becomes a
frame, the body takes the slack and scrolls inside it, and the foot is pinned
under both. Reach for it when the content has no ceiling — a list as long as
the subtree, a description as long as the brief — and leave it off otherwise,
because a dialog of four fields is better as one block than as three. It is a
modifier and not a new default for the same reason: the dialogs that pass no
footer are laid out against the panel being the scroller, and they stay that
way.

### Pages that are not React apps

Two pages in the house cannot render `<TopBar>`: **Swagger UI**, which is
Swashbuckle's document that Aerie reaches only through injected `<head>`
content, and **`apps/logo`**, a static export whose runtime loads its own React
from a CDN. Both were one-way trips from the app picker.

`@aerie/ui/standalone/topbar` is the same bar built with DOM calls, and
[`apps/chrome`](../src/Aerie.Web/apps/chrome/README.md) is the build that emits
it as `topbar.js` + `topbar.css` under `/apps/chrome/`:

```html
<link rel="stylesheet" href="/apps/chrome/topbar.css">
<script>window.aerieTopBar = { appName: 'Aerie Logo', theme: 'light' };</script>
<script src="/apps/chrome/topbar.js"></script>
```

`theme` is `switch` (the page is themed — render the Auto/Light/Dark control) or
`light`/`dark` (the page has one appearance — pin the root to it and render no
control). Swagger takes `switch`, because it is dressed from the same tokens and
the control is therefore telling the truth about the whole page. The logo export
takes `light`: it is a fixed cream design, and a toggle there would recolor a
48px bar and leave the page behind it.

What is shared is the stylesheets — the *same* `TopBar.css`, `AppSwitcher.css`
and `ThemeSwitch.css` files the React components import — and the theme store.
What is restated is about twenty elements of markup. **The rule that keeps the
two in step: the standalone file may not invent a class name.** Every class in
it appears in one of the three React components. A change to the CSS reaches
both; a change to the *markup* has to be made twice, and that is the price of
not shipping React to a static page.

`base.css` is deliberately not imported there. It paints the body, and these are
host pages with their own designs; the bar dresses itself and touches nothing
outside its own `<header>`.

## The gallery

[`apps/design`](../src/Aerie.Web/apps/design/README.md), served at
`/apps/design`, is every token and component rendered in both themes and in the
states an app never shows you.

It is a workbench, not a product surface. Its purpose is that a component built
after it exists is developed *there* — against its own disabled, loading, error,
empty and long-content states — rather than inside a page of admin where only
the happy path is visible.

### Adding a section

One entry in [`src/sections.ts`](../src/Aerie.Web/apps/design/src/sections.ts):

```ts
{ group: 'Components', slug: 'toolbar', title: 'Toolbar', Page: ToolbarPage },
```

That puts it in the nav, on a route and behind a deep link. **Nothing else in
the app enumerates the sections** — the nav groups are derived from the entries
in first-appearance order, and `/` and any unknown route redirect to the first
one. A group appears when it has something in it, rather than sitting empty in
the nav as a promise the app cannot keep.

Within `Components` the order is the order a page is built — the chrome, then
the frame, then what goes in it, then what it says — rather than alphabetical,
so a designer reading the nav top to bottom reads it in the order the decisions
compound.

A page is built from three helpers in
[`components/Gallery.tsx`](../src/Aerie.Web/apps/design/src/components/Gallery.tsx):
`<GalleryPage>` (title, one line saying what the group is for, the specimens),
`<GallerySection>` (a named run, whose `note` carries **the rule the run exists
to state** — the rule is the part a designer needs and the swatch is only the
evidence), and `<TokenName>`/`<TokenValue>`.

### Three rules the gallery is held to

- **It reads the values the browser resolved** rather than restating them, so a
  swatch can never disagree with the token it claims to show. That is
  [`lib/useTokenValues.ts`](../src/Aerie.Web/apps/design/src/lib/useTokenValues.ts).
- **It holds no component CSS of its own.** It shows a component by rendering
  it, never by restating its styles. The only rules in `App.css` are the
  gallery's own chrome and the frames its specimens sit in. A gallery whose copy
  of a component's CSS can drift from the component is a gallery that lies.
- **The specimens are live.** The buttons press, the fields take typing, the
  modals take over the page and give focus back. A specimen that mocks its own
  behaviour is a specimen that can be wrong about it.

And it wears the bar it documents, which is not decoration: the one context a
top bar is never shown in on a specimen page is an actual app, and this is the
app that can show both at once.

## The plumbing tax: a new app, in six places

A new app under `apps/` costs six edits outside its own folder. This is the list,
and it has been paid twice knowingly — by `apps/design` and by `apps/home` — so
it is a checklist rather than an estimate.

1. **`src/Aerie.Api/Aerie.Api.csproj`** — an `<AppName>Source` `ItemGroup` and a
   `Build<AppName>` target `BeforeTargets="Build" DependsOnTargets="NpmInstall"`,
   guarded by `Condition="'$(Skip<AppName>Build)' != 'true'"`, running
   `npm run build -w apps/<app>` from `../Aerie.Web`. `NpmInstall` is the only
   target that shells out to `npm install`, and it does it once at the workspace
   root. **The `Inputs` glob stays per-app** — that is what keeps an edit to one
   app from rebuilding all the others — so an app consuming a shared package must
   include `../Aerie.Web/packages/*/src/**/*.*` in its own glob. Spelled
   `packages/*/src` rather than `packages/**` so a stray `node_modules` under a
   package can never widen it.
2. **`src/Aerie.Api/Dockerfile.api`** — a `COPY` of the app's `package.json` into
   the `web-build` stage *ahead of* `npm ci`, and `-p:Skip<AppName>Build=true` on
   the `dotnet publish` line. The first is not optional: `npm ci` reads the
   lockfile, finds a `link:` entry for every workspace, and fails outright if
   that manifest is not on disk. The list is ten app manifests plus both
   packages.
3. **`src/Aerie.Api/Program.cs`** — `AddRedirect("^apps/<app>$", "apps/<app>/")`,
   and `MapFallbackToFile("/apps/<app>/{*path:nonfile}", …)` **only if the app
   has a client-side router**. `apps/home` deliberately has no fallback: it is
   one page, and a fallback would answer every mistyped path under `/apps/home/`
   with the picker and a 200 instead of the 404 that path deserves.
4. **`.github/workflows/ci.yml`** — the `app:` matrix.
5. **`Makefile`** — the `test-web` app list. The apps are named one at a time
   rather than run with `--workspaces` so the `==>` line says which one is
   building when something fails.
6. **`src/Aerie.Web/apps/home/src/apps.ts`** — a tile, in one of the three tiers.
   Nothing else in the picker enumerates the services.

Inside the app's own folder: `package.json` (name, the standard
`dev`/`build`/`lint`/`preview` scripts, `"@aerie/ui": "*"`), and a
`vite.config.ts` with `base: '/apps/<app>/'`, `outDir` pointing at
`src/Aerie.Api/wwwroot/apps/<app>`, `emptyOutDir: true`, and the
`aerieRevision({ app: '<app>' })` plugin.

One trap, because it has cost time before: **`.gitignore`'s NuGet
`**/[Pp]ackages/*` rule is a path glob, not a NuGet-aware one**, and it
silently swallowed `src/Aerie.Web/packages/` — `git add` reported nothing and
`git status` stayed clean. It is negated explicitly now, but anything new under
a directory called `packages` wants a `git check-ignore` before it is trusted.

Two apps are deliberately or accidentally outside part of that list, and both
are worth knowing before reading the csproj as the definitive one.
`apps/trading` has no MSBuild target because its bundle is served by the trading
service and builds into `src/Aerie.Trading/`, not into `wwwroot/apps`.
`apps/hatch` has none either — it is built by `make test-web` and, in the
release image, by the `web-build` stage's `npm run build --workspaces`, but a
local `make build` does not produce it. That is a gap rather than a decision.

Build with `make` (`make build`, `make test-web`) rather than a bare `dotnet`:
the npm step needs the shell profile.

## Tripwires

The properties above stop holding if any of these is done:

- **A literal color, radius, type size or spacing value in an app's CSS.** The
  first one is free and invisible; the tenth is a design pass that has to grep.
- **A token defined only inside a media query.** It vanishes in the other theme
  and nothing says so.
- **The two dark blocks in `tokens.css` drifting apart.** They are the same
  palette written twice; an edit to one is an edit to both.
- **A class name invented in `standalone/topbar.ts`.** The bar's two builds stay
  in step because the CSS has one source and the standalone file only *uses*
  what the components declare.
- **Component CSS copied into the gallery.** A gallery that restates a component
  can be wrong about it, and it will be wrong exactly when it matters.
- **An app consuming `@aerie/ui` without `packages/*/src` in its MSBuild
  `Inputs`.** The build stays green and ships a stale palette.
- **React imported into `@aerie/lib`, or into `theme/themeStore.ts`.** The first
  breaks the package's usability from a test or a worker; the second puts React
  on Swagger UI.
- **Extending `<TopBar>` by forking it.** It takes `leading` and `trailing` for
  exactly this reason.

## See also

- [`docs/ethos.md`](ethos.md) — the rule that constrains every commit, and which
  applies to gallery sample content as hard as to the apps: a `<Card>` example
  says "Living Room", never a real room in a real house, and never a real domain.
- [`src/Aerie.Web/apps/design/README.md`](../src/Aerie.Web/apps/design/README.md)
  — the gallery, app-side.
- [`src/Aerie.Web/apps/home/README.md`](../src/Aerie.Web/apps/home/README.md) —
  the picker, and why it stopped being a static file.
- [`src/Aerie.Web/apps/chrome/README.md`](../src/Aerie.Web/apps/chrome/README.md)
  — the standalone bar's build.
- [`src/Aerie.Web/packages/lib/README.md`](../src/Aerie.Web/packages/lib/README.md)
  — what earns a place in the shared logic package.
- [`docs/family-apps-architecture.md`](family-apps-architecture.md) — the same
  "app #2 costs a folder and an afternoon" goal, one layer down.
- [`docs/kiosk-architecture.md`](kiosk-architecture.md) — the circadian engine
  this system deliberately does not adopt.
