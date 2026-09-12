# Design System Architecture

## Summary

Hatch's web apps share one vocabulary and one set of components, both living in
**`@hatch/ui`** — a package inside the `src/Hatch.Web` npm workspace, consumed
by every app that renders house chrome.

The design goal is a *property*, not a style guide.
The property is that **a color, radius, type step or spacing value written as a
literal in an app is a bug** — there is one place those values live, a designer
can change them there, and every app changes with them. Nothing enforces that
mechanically. What makes it hold is that the alternative is genuinely more work:
the token is already named, already has an answer in both themes, and is already
on a page you can look at.

Three things follow from it and are the rest of this document:

- **The vocabulary** — what is a token, what is not, and why the list is closed.
- **The day/night contract** — one stored choice, honoured by React apps and by
  pages that are not apps, resolved with no JavaScript in the common case.
- **The gallery** — `apps/design`, where every token and component is rendered
  in both themes and in the states an app never shows you.

The mechanics of each consuming app live next to that app, in its own README.
What is here is the part no single app owns.

**What this document does not do is decide what Hatch looks like.** Every value
in `tokens.css` is a carried-across, considered starting point, not a final
answer. The four-step radius scale, the named type registers and the single
spacing base are the *philosophy*; the numbers in them are placeholders with a
home. A design pass moves the numbers, and the whole point of the structure
below is that moving them is an edit to one file rather than a rewrite of every
app.

## The workspace

`src/Hatch.Web` is a single npm workspace: `apps/*` and `packages/*`, one
hoisted `package-lock.json` at the root, one `node_modules`.

```
src/Hatch.Web/
  package.json          workspaces: ["apps/*", "packages/*"]
  package-lock.json     one lockfile, hoisted
  apps/
    design  hatch
  packages/
    ui                  @hatch/ui   — the vocabulary and the components
```

### Who consumes what

| App | `@hatch/ui` | Note |
|---|---|---|
| `design` | yes | The gallery |
| `hatch` | yes | Adopted the bar and the tokens |

Both apps use the same token names (`--bg`, `--card`, `--ink`, `--muted`,
`--line`, `--primary`…), so a future app can carry its own values under those
names and adopt `@hatch/ui` as a stylesheet swap rather than a rewrite.

## The vocabulary

[`packages/ui/src/tokens.css`](../src/Hatch.Web/packages/ui/src/tokens.css)
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

One stored choice — `auto | light | dark` — under one key, `hatch.theme`,
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

[`theme/themeStore.ts`](../src/Hatch.Web/packages/ui/src/theme/themeStore.ts) is
the whole mechanism — read, write, resolve, apply, watch — with **no React in
it**. [`theme/ThemeProvider.tsx`](../src/Hatch.Web/packages/ui/src/theme/ThemeProvider.tsx)
is only the React around it: the state components read, the subscription, the
context.

The split exists because there are two callers. The other is
[`standalone/topbar.ts`](../src/Hatch.Web/packages/ui/src/standalone/topbar.ts),
the bar on pages Hatch did not build with Vite. Those pages must read and write
the *same* stored choice as the apps — a house where the theme you picked in
one app does not survive the click into Swagger is a house with two themes —
and importing `ThemeProvider` to get it would drag React onto a static page to
run twelve lines of `localStorage`.

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

## Adopting `@hatch/ui`

### The wiring

An app's own stylesheet takes the vocabulary and the document layer:

```css
/* apps/<app>/src/theme.css */
@import '@hatch/ui/tokens.css';
@import '@hatch/ui/base.css';
```

and the entry point provides the theme and the bar:

```tsx
/* apps/<app>/src/main.tsx */
import { ThemeProvider } from '@hatch/ui';
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
import { TopBar } from '@hatch/ui';
<TopBar appName="Hatch <App>" />
```

Add `"@hatch/ui": "*"` to the app's `dependencies` — a workspace link, so
editing a token hot-reloads with no publish step — and add
`../Hatch.Web/packages/*/src/**/*.*;../Hatch.Web/packages/*/package.json` to the
app's MSBuild `Inputs` glob. Without that last line a token edit will not
retrigger the app's incremental build, and the app will ship a stale palette
from a build that looked successful.

### Two entry points, because they are two kinds of thing

`tokens.css` states values and paints nothing. `base.css` paints: the box model,
the body's face and ground, the heading scale bound to the type registers, and
the native form controls.

The native controls are why `base.css` exists at all rather than being a
convenience. `<Field>` renders a label around a control the *app* supplies — a
raw `<input>`, `<select>`, `<textarea>`. If those rules lived only in one
consuming app's own stylesheet, a `<Field>` specimen in the gallery would
render a naked browser input, and a gallery that shows a component looking
different from how it looks in the app is worse than no gallery.

Two rules are deliberately **not** in `base.css`, and each app states its own:

- **`p { margin: 0 }`.** An app laid out against the browser's default
  paragraph margins, with its own utilities stacked on top, and the gallery
  making the opposite call, are both legitimate choices. Zeroing it centrally
  would move text on every page of the first kind of app.
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

The nine primitives after the chrome are extracted from CSS conventions that
already existed and repeated across the house's apps, rather than invented.
That is why the set is small and why it is the right small set.

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

`@hatch/ui/standalone/topbar` is `<TopBar>` built with DOM calls instead of
React, for a page that cannot import the React component — Swagger UI
(Swashbuckle's own document, reached only through injected `<head>` content)
is the one currently in the house, and a static export would be another. It is
kept in the library even while nothing currently wires it up, for the next
page that needs it:

```html
<link rel="stylesheet" href="topbar.css">
<script>window.hatchTopBar = { appName: 'Hatch API', theme: 'switch' };</script>
<script src="topbar.js"></script>
```

`theme` is `switch` (the page is themed — render the Auto/Light/Dark control) or
`light`/`dark` (the page has one appearance — pin the root to it and render no
control).

What is shared is the stylesheets — the *same* `TopBar.css`, `AppSwitcher.css`
and `ThemeSwitch.css` files the React components import — and the theme store.
What is restated is about twenty elements of markup. **The rule that keeps the
two in step: the standalone file may not invent a class name.** Every class in
it appears in one of the three React components. A change to the CSS reaches
both; a change to the *markup* has to be made twice, and that is the price of
not shipping React to a static page.

`base.css` is deliberately not imported there. It paints the body, and a host
page like this has its own design; the bar dresses itself and touches nothing
outside its own `<header>`.

## The gallery

[`apps/design`](../src/Hatch.Web/apps/design/README.md), served at
`/apps/design`, is every token and component rendered in both themes and in the
states an app never shows you.

It is a workbench, not a product surface. Its purpose is that a component built
after it exists is developed *there* — against its own disabled, loading, error,
empty and long-content states — rather than inside a page of a consuming app
where only the happy path is visible.

### Adding a section

One entry in [`src/sections.ts`](../src/Hatch.Web/apps/design/src/sections.ts):

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
[`components/Gallery.tsx`](../src/Hatch.Web/apps/design/src/components/Gallery.tsx):
`<GalleryPage>` (title, one line saying what the group is for, the specimens),
`<GallerySection>` (a named run, whose `note` carries **the rule the run exists
to state** — the rule is the part a designer needs and the swatch is only the
evidence), and `<TokenName>`/`<TokenValue>`.

### Three rules the gallery is held to

- **It reads the values the browser resolved** rather than restating them, so a
  swatch can never disagree with the token it claims to show. That is
  [`lib/useTokenValues.ts`](../src/Hatch.Web/apps/design/src/lib/useTokenValues.ts).
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

## The plumbing tax: a new app, in several places

A new app under `apps/` costs edits outside its own folder, each paid once
already by `apps/design` — the only other app to join `apps/hatch` so far — so
this is a checklist rather than an estimate.

1. **`src/Hatch.Api/Hatch.Api.csproj`** — an `<AppName>Source` `ItemGroup` and a
   `Build<AppName>` target `BeforeTargets="Build" DependsOnTargets="NpmInstall"`,
   guarded by `Condition="'$(Skip<AppName>Build)' != 'true'"`, running
   `npm run build -w apps/<app>` from `../Hatch.Web`. `NpmInstall` is the only
   target that shells out to `npm install`, and it does it once at the workspace
   root. **The `Inputs` glob stays per-app** — that is what keeps an edit to one
   app from rebuilding the other — so an app consuming a shared package must
   include `../Hatch.Web/packages/*/src/**/*.*` in its own glob. Spelled
   `packages/*/src` rather than `packages/**` so a stray `node_modules` under a
   package can never widen it.
2. **`src/Hatch.Api/Dockerfile.api`** — a `COPY` of the app's `package.json` into
   the `web-build` stage *ahead of* `npm ci`, and `-p:Skip<AppName>Build=true` on
   the `dotnet publish` line. The first is not optional: `npm ci` reads the
   lockfile, finds a `link:` entry for every workspace, and fails outright if
   that manifest is not on disk.
3. **`src/Hatch.Api/Program.cs`** — `AddRedirect("^apps/<app>$", "apps/<app>/")`
   and `MapFallbackToFile("/apps/<app>/{*path:nonfile}", …)` for an app with a
   client-side router.
4. **`.github/workflows/ci.yml`** — the `app:` matrix.
5. **`Makefile`** — the `test-web` app list. The apps are named one at a time
   rather than run with `--workspaces` so the `==>` line says which one is
   building when something fails.

Inside the app's own folder: `package.json` (name, the standard
`dev`/`build`/`lint`/`preview` scripts, `"@hatch/ui": "*"`), and a
`vite.config.ts` with `base: '/apps/<app>/'`, `outDir` pointing at
`src/Hatch.Api/wwwroot/apps/<app>`, `emptyOutDir: true`, and the
`hatchRevision({ app: '<app>' })` plugin.

One trap, because it has cost time before: **`.gitignore`'s NuGet
`**/[Pp]ackages/*` rule is a path glob, not a NuGet-aware one**, and it
silently swallowed `src/Hatch.Web/packages/` — `git add` reported nothing and
`git status` stayed clean. It is negated explicitly now, but anything new under
a directory called `packages` wants a `git check-ignore` before it is trusted.

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
- **An app consuming `@hatch/ui` without `packages/*/src` in its MSBuild
  `Inputs`.** The build stays green and ships a stale palette.
- **React imported into `theme/themeStore.ts`.** That puts React on Swagger UI.
- **Extending `<TopBar>` by forking it.** It takes `leading` and `trailing` for
  exactly this reason.

## See also

- [`docs/ethos.md`](ethos.md) — the rule that constrains every commit, and which
  applies to gallery sample content as hard as to the apps: a `<Card>` example
  says "Living Room", never a real room in a real house, and never a real domain.
- [`src/Hatch.Web/apps/design/README.md`](../src/Hatch.Web/apps/design/README.md)
  — the gallery, app-side.
