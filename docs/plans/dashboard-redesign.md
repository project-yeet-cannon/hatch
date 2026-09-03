# Dashboard redesign — two pages, one design language

**Status:** Phases 0–5 done — the wall builds and tests green as two pages
with the climate card; what remains is Phase 6, the owner's hardware pass,
plus the owner-eyeball items noted inside Phases 1/3/4. **The stage this plan
designed has since been retired — see [Amendment: the column replaces the
stage](#amendment-the-column-replaces-the-stage).** The
follow-up section (the API's half of the contract) stays open by design.
**This plan is design-only on the data side**: everything new renders from the
mock first, and the API work it needs is specified at the end under
[Follow-up: the data contract to fulfill](#follow-up-the-data-contract-to-fulfill)
— deliberately not part of this plan's execution.

## Amendment: the column replaces the stage

*Owner change request, after living with it.* The stage lost the argument the
decision table made for it: the agenda was behind a horizontal swipe, and a
wall you walk past should not have to be asked what is on today. Photos and
the agenda no longer share a region — page one is a plain vertical column,
**climate card · agenda · photo frame**, and the shared column got wider at
the same time.

What changed, against everything below:

- **`components/Stage.tsx` is gone**, with `lib/stageFaces.ts` and
  `lib/agendaGlance.ts` and their tests. There is no track, no faces, no dots,
  no `.hf-stage*` rule. The existence matrix survives as the two components'
  own renders-nothing rules, composed by the column: `calendarHasContent`
  gates the agenda, `PhotoCarousel` returns null on an empty deck.
- **The agenda is the full `CalendarSection`, moved, not copied.** It ran
  capped at four rows because the face was a fixed-height box that could not
  scroll; the column scrolls, so the cap had no reason left to exist. Page two
  no longer carries an Agenda section — the wall has exactly one agenda, and
  it is above the fold. `.hf-agenda` gives it the card chrome its two
  neighbours already had; `EventBlock` is private to `CalendarSection` again.
- **The photo frame is a plain `.hf-photo` again**, at the column's full
  width, with the card chrome it ceded to the stage handed back.
- **The column is as wide as the wall.** `clamp(480px, 75vw, 960px)` with
  20px of padding resolved to 600px on the 800px walls — 200px of bare
  background down the sides for a device whose entire job is to show content.
  It is `max-width: 1280px` (a bound no wall ever meets — it is there for a
  desktop browser) with a 10px gutter, so the walls run 780px of content
  instead of 560.
- **The fold budget below is history.** Three blocks stacked at the full
  width do not fit 1280px on a day with events, and that is the trade the
  change request makes deliberately: page one scrolls into page two rather
  than hiding the agenda behind a gesture. Page one keeps `min-height:
  100dvh` and page two keeps its snap point, so the pager, the idle reset's
  `scrollTo(0, 0)` and the lifecycle's scroll-presence signal are untouched.

Everything else in this plan — the token pass, the climate card, the section
header idiom, page two's remaining sections — stands as written.

## The ask

The MVP dashboard was one considered design: climate cards and a date/time
header, on the circadian palette. Everything since — agenda, alerts, routines,
cameras, panels, Gather, photos, the health dot — landed as one-offs without a
design pass. The redesign, in the owner's priority order:

- **Consistency.** Make the newer components speak the original design
  language first, then rethink how the components are organized.
- **Beauty and simplicity.** Keep the spirit of the original work. Exploring
  is fine; sliding into generic AI-dashboard ruts (KPI tile walls, gauge
  clusters, glassmorphism) is not.
- **Above the fold, portrait**: the error/health signal
  ([Degrading gracefully](../kiosk-architecture.md#degrading-gracefully) — landed);
  date/time as prevalent as today or more; climate cards — outside plus 2–3
  interior zones — with the day's/tomorrow's forecast shown as simply as
  possible, outdoor air quality, and weather alerts; the Immich photo
  carousel; today's calendar.
- Everything else the page does is secondary and should stay simple while
  leaving room to grow.

Four decisions made with the owner up front:

| Question | Decision |
|---|---|
| Forecast + AQI aren't in the snapshot today | **Design against the mock.** Extend the client contract and mock sources now; the API work is a spec'd follow-up, not a phase |
| Photos and today's agenda both want the fold | **A shared stage.** One large region; photos are the resting face, a horizontal swipe flips to today's agenda — *superseded, see the amendment below* |
| Which 2–3 zones lead | **The admin picks.** A per-zone "lead" flag; unpinned zones still render, just not above the fold |
| Where routines/cameras/panels/Gather live | **A second page, one swipe up.** Page one is the wall at rest; page two is the wall you walk up to |

## What the wall shows today, and what the audit found

The column, top to bottom ([App.tsx](../../src/Aerie.Web/apps/dashboard/src/App.tsx)):
header (date left, 108px clock right) · [AlertBanner](../../src/Aerie.Web/apps/dashboard/src/components/AlertBanner.tsx)
(nothing on calm days) · [OutsideCard](../../src/Aerie.Web/apps/dashboard/src/components/OutsideCard.tsx)
(always expanded) · one collapsed [ZoneCard](../../src/Aerie.Web/apps/dashboard/src/components/ZoneCard.tsx)
`<details>` row per zone · [PhotoCarousel](../../src/Aerie.Web/apps/dashboard/src/components/PhotoCarousel.tsx)
· [CalendarSection](../../src/Aerie.Web/apps/dashboard/src/components/CalendarSection.tsx)
· routines, cameras, panels as three unlabeled tile grids · Gather tiles.
Overlays (Gather, panel, camera, health) are consistent with each other and
stay as they are.

What "slapdash" concretely is, in
[theme.css](../../src/Aerie.Web/apps/dashboard/src/theme.css):

1. **Six radii pretending to be a system.** 26 (cards) / 22 (health reload) /
   18 (controls) / 14 (calendar blocks, Gather icon squares, Gather clear) /
   12 (skeleton chart) / 10 (qty chips, Gather checkbox) / 6 (skeleton).
2. **A type ramp full of strays.** Tile names at 16 next to zone names at 19;
   Gather pick-counts at 24 next to tile counts at 30; meta text at 12, 13,
   15, and 16 with no rule for which.
3. **Spacing by feel.** Column gap 13, zone gap 10, calendar gap 15, grids 16;
   card paddings 11/16, 12/14, 12/18, 14/18, 15/17, 16/18.
4. **Sections without names.** The agenda invented a good section-header idiom
   (uppercase 12px label + hairline rule); routines, cameras, panels and
   Gather are four adjacent grids of near-identical square tiles with no
   wayfinding at all — the "row breaks" App.tsx comments on are invisible.
5. **Inline style patches** where the CSS didn't fit:
   [OutsideCard](../../src/Aerie.Web/apps/dashboard/src/components/OutsideCard.tsx)
   carries `width: 'auto'`, ad-hoc margins, and an inline color override;
   App.tsx patches `margin: 0` onto the error note.
6. **Chrome emoji.** `☀` in the outside stats row is the one emoji not chosen
   by a person (Gather/panel icons are user data and stay). Routines already
   resolve Font Awesome names through
   [icons.ts](../../src/Aerie.Web/apps/dashboard/src/lib/icons.ts).

And two asks the data can't serve yet, which is why the mock leads:

- [WeatherService.cs](../../src/Aerie.Api/Services/Dashboard/WeatherService.cs)
  returns `Forecast: []` and `Hourly: []` — the real wall has never drawn a
  forecast line, and nothing anywhere says what tomorrow looks like.
- [OpenMeteoAirQualityProvider.cs](../../src/Aerie.Api/Services/Hazards/OpenMeteoAirQualityProvider.cs)
  already fetches current/peak/hourly US AQI, but it reaches the wall only as
  a hazard alert when air is bad. There is no always-on reading.

## Design commitments

The rules every phase below is written against, and the review bar for any
deviation a downstream actor is tempted by:

1. **The circadian system is the design.** Every color is either a palette
   token from [circadianTheme.ts](../../src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts)
   or a fixed semantic hue mixed into `var(--card)` (the
   AlertBanner/health-dot precedent — hue survives the night, luminance does
   not). No third form. The keyframe table in
   [tokens.ts](../../src/Aerie.Web/apps/dashboard/src/theme/tokens.ts) is
   load-bearing on the Kotlin side
   ([CircadianBrightness.kt](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/CircadianBrightness.kt))
   and this plan does not touch it.
2. **Calm.** Nothing moves unless a person moved it or a photo dissolved on
   its dwell. No auto-flipping faces, no attention-seeking transitions, no
   layout that shifts on a poll.
3. **Renders-nothing discipline.** Every feature that isn't configured or has
   nothing to say renders nothing — no dead tiles, no empty frames, no "no
   events" cards on a house with no calendar. This already holds everywhere;
   the new composites must keep it.
4. **Degradation stays honest.** The staleness states
   (fresh/stale/none), the health dot, and the boot fallback from
   [Degrading gracefully](../kiosk-architecture.md#degrading-gracefully) survive the redesign with
   their semantics intact — dimmed numbers stay dimmed, "no data" stays
   loud enough to see, and nothing new invents its own error styling.
5. **Portrait, arm's length, top-half inputs.** The wall tablets are portrait
   and immersive ([kiosk-architecture.md](../kiosk-architecture.md)); anything
   that raises a keyboard keeps its input *and* its buttons in the top half.
   Nothing in this redesign adds an input. Landscape stays out of scope.
6. **Portable.** No hardcoded domains, family names, or place names in
   markup, mock data included ([ethos.md](../ethos.md)). The mock's calendar
   says "Piano lesson", not a real child's name.

## The design

### Two pages, one document

Page one is everything the wall is for at a glance and **fits the viewport
with no scrolling on a calm day**. Page two is everything you walk up to it
for. The pager is not a router and not a gesture library — it is the document
itself with CSS scroll snap, which keeps every existing mechanism (the
lifecycle's `scroll` presence signal, `window.scrollTo(0, 0)` on idle reset,
overlay holds) working unchanged:

```css
html { scroll-snap-type: y proximity; }
.hf-p1 { min-height: 100vh; min-height: 100dvh; scroll-snap-align: start; }
.hf-p2 { min-height: 100vh; min-height: 100dvh; scroll-snap-align: start; }
```

- `proximity`, not `mandatory`: page two will outgrow one viewport
  ("continued growth" is in the ask), and mandatory snap makes resting
  mid-page impossible. Proximity gives the swipe-up-a-page feel at the
  boundary and free scrolling inside a tall page two. If page two stays short
  in practice, flipping to `mandatory` is a one-word change Phase 6 can make
  on feel.
- Both pages are children of the existing `.hfdev` column, so the max-width
  clamp and the circadian custom properties on `.hf-page` keep applying to
  everything.
- The idle reset already calls `window.scrollTo(0, 0)`
  ([useKioskLifecycle.ts](../../src/Aerie.Web/apps/dashboard/src/hooks/useKioskLifecycle.ts))
  — with snap, that lands exactly on page one. No new reset code for the
  pager itself.
- **The swipe hint**: a 36×5px pill, centered, `border-radius: 3px`,
  `background: color-mix(in srgb, var(--ink) 18%, transparent)`, sitting in
  page one's bottom padding. Tapping it scrolls page two into view
  (`scrollIntoView`, smooth unless `prefers-reduced-motion`). It renders only
  when page two has content. It sits in the bottom half of the screen, which
  is fine: the top-half rule exists for keyboard overlap, and this is not an
  input and no keyboard can be up while page one is at rest.
- Page two exists only if it has content (`routines`, `cameras`, `panels`,
  Gather lists, unpinned zones, or agenda) — otherwise no `.hf-p2`, no snap
  point, no hint. A fresh deployment with nothing configured is a one-page
  wall, which is the truth.

### The token pass (Phase 1 makes this real)

**Radii** — four names, stated in `:root`, every rule migrated to one of them:

| Token | Value | Used by |
|---|---|---|
| `--r` | 26px | Cards, tiles, the stage, overlays' large surfaces (unchanged) |
| `--r-ctl` | 18px | Every control: buttons, inputs, tappable rows, icon buttons, power buttons, the health reload (22→18), panel steppers (26→18), Gather clear (14→18) |
| `--r-in` | 14px | Nested content blocks: calendar event blocks (14 ✓), Gather icon squares (14 ✓), full-size skeleton charts (12→14) |
| `--r-chip` | 10px | Micro chips: qty (✓), Gather checkbox (✓), small skeletons (6→10) |

Badges stay fully-rounded pills (`border-radius: 999px`, replacing the magic
`30px`).

**Type** — named registers; the sweep maps every stray to its nearest
register and no size outside the table survives:

| Register | Spec | Where |
|---|---|---|
| Clock | `clamp(88px, 18.5vw, 112px)`/0.85, 700, −0.03em, tabular | The clock (was fixed 108 — the clamp is the narrow-viewport insurance Phase 0 prices) |
| Hero | 44/1, 700, −0.02em, tabular | The climate pane's selected temperature |
| Display | 34/1, 800 + 30/1, 700 | The date block (unchanged) |
| Title | 27/1.2, 700 | Overlay titles (unchanged) |
| Count | 30/1, 800, tabular | Gather tile counts (24px pick-counts → 22 Value) |
| Value | 22/1, 600, tabular | Climate tab temps, Gather pick counts |
| Name | 19/1.2, 700 | Zone/card names, wide-tile names |
| Name-sm | 17/1.2, 700 | Square-tile names (16→17), calendar event titles (✓) |
| Body-touch | 21/1.25, 600 | Gather rows — arm's-length register (unchanged) |
| Meta | 15/1.3, 500 · 13/1.3, 500 | Secondary lines · footnotes ("16px meta" → 15) |
| Label | 12/1, 700, 0.09em, uppercase | Section headers, badges, axis labels |
| Micro | 11/1, 600, 0.04em, uppercase | On/off states, tiny annotations |

**Spacing** — base 6. The scale is 6/12/18/24/36: column and section gaps 12
(13→12, 10→12), grid gaps 18 (16→18), card padding 18, compact-row padding
12 vertical / 18 horizontal (11/16 and 12/14 migrate), page padding
`24px 20px`. Values under 6px (the 2–4px optical nudges) are free.

**The section header idiom** — the agenda's header generalizes as
`.hf-sec-head`: Label-register text + hairline rule, 24px space above the
section, 12 below the header. Page two uses it for every section; page one
uses it nowhere (page one's composites are self-evident and headers would be
furniture).

**Iconography rule** — chrome icons are Font Awesome through
[icons.ts](../../src/Aerie.Web/apps/dashboard/src/lib/icons.ts) (the routine
tiles' existing path); emoji appear only where a person typed one (Gather list
icons, panel icons). The `☀` in the outside stats goes; the outlook glyphs
below come from FA.

### Page one

Order: health dot (fixed, unchanged) · header · alerts · **climate card** ·
**the stage** · swipe hint. *(The stage is retired — the shipped order is
climate card · agenda · photo frame; see the amendment.)*

**Header** — structurally unchanged (date block left, clock right); the clock
takes the Clock register's clamp. It is already the loudest thing on the page
and stays that way.

**Alerts** — [AlertBanner](../../src/Aerie.Web/apps/dashboard/src/components/AlertBanner.tsx)
unchanged, above everything but the header, rendering nothing on calm days.
On an alert day it pushes the stage toward the fold, which is the correct
trade: a hazard displaces the photo frame, not the other way around.

**The climate card** — the centerpiece change. Outside plus every *lead* zone
in **one card**: a tab row of miniature readings across the top, one reading
pane below. This is what buys the fold back (four stacked cards ≈ 530px
become ≈ 360px) while giving every zone the full-size chart today's collapsed
rows never show, and the tabs are deliberately the old collapsed zone row,
miniaturized — swatch, name, temperature — so the continuity is legible.

```text
┌─ .hf-climate ──────────────────────────── (--r, --card, hairline, pad 18) ┐
│ .hf-climate-tabs      role=tablist, flex row, gap 6                       │
│   .hf-ctab ×(1+N)     flex:1 min-width:0, pad 10 12, radius --r-ctl      │
│     swatch 10px · name 15/700 ellipsis                                    │
│     temp Value-register ('—' at none; opacity .45 at stale)               │
│     selected: background color-mix(in srgb, var(--ink) 8%, var(--card))   │
│ ── hairline, margin 12 0 ──                                               │
│ .hf-climate-pane      keyed by selection                                  │
│   head row: Hero temp · condition (outside: the weather note; zone: the   │
│             b-* status badge) · right-aligned as-of / "no data" badge     │
│   TempChart  full variant, height 110 (was 130) + TempChartAxis           │
│   .hf-foot stats row                                                      │
│     outside: humidity · sunset · rain window (when >0) · AQI pill         │
│     zone:    low · high (existing content)                                │
│   .hf-outlook  (outside pane only) two equal cells over a hairline:       │
│     "TODAY  72°/58° ⛅ᶠᵃ"  ·  "TOMORROW  78°/61° ⛅ᶠᵃ"                     │
│     cell: Label register word · 17/700 hi/lo · 16px FA condition glyph    │
└───────────────────────────────────────────────────────────────────────────┘
```

- **Selection** is `useState`, defaulting to Outside; the card is keyed on
  `resetToken` so the idle reset returns it to Outside — the same remount
  pattern the `<details>` collapse uses today. A poll that drops the selected
  zone falls back to Outside by the `openPanel` pattern.
- **Forecast, intuitively**: the chart *is* today's forecast — history solid,
  forecast dashed, the idiom already built — and tomorrow is one glanceable
  cell. No hourly strip, no second chart, no icon rows.
- **The AQI pill** reuses the badge anatomy. Band → color follows the
  severity precedent (fixed hue mixed into `--card`): Good renders in the
  comfort accent's `b-ok` styling; Moderate `#d9a441`; UnhealthyForSensitive
  `#e07a3f`; Unhealthy and worse `#d14343`. Text: `AQI 42 · Good`. Absent
  when the snapshot has no reading; muted when the reading is stale (>2h).
- **Staleness carries over intact**: a stale selection dims its Hero temp and
  chart to the existing `.stale` opacity and shows "as of 3:42 PM" in the
  head row; `none` shows `—`, the "no data" badge, and the flat muted band
  via the existing
  [zonePresentation.ts](../../src/Aerie.Web/apps/dashboard/src/lib/zonePresentation.ts)
  path. The *tab* of a stale zone dims its temperature; a `none` tab shows
  `—` with a muted swatch — the wall never hides which room went quiet.
- **Lead zones** are `zones.filter(z => z.pinned)` in snapshot order. Zero
  pinned (fresh install, or the API predating the flag) → the first two zones
  lead, so the card never renders empty-tabbed. Tabs stay comfortable to 4
  total (Outside + 3); more pinned than that squeeze by ellipsis rather than
  wrap — the admin holds the taste knob.
- Zones *not* pinned keep today's `<details>` ZoneCard **unchanged**, on page
  two under "More rooms" — the original interaction survives where a
  scrolling list is the right shape.
- `OutsideCard` and page-one `ZoneCard` usage retire; `TempChart`,
  `zonePresentation`, `staleness` are reused as-is.

**The stage** — one region, photo-frame proportions, two faces. *Retired;
kept here as the record of what was built and why it was replaced — see
[the amendment](#amendment-the-column-replaces-the-stage).*

```text
.hf-stage        aspect-ratio 3/2 · radius --r · hairline border · --card
                 overflow hidden · position relative
.hf-stage-track  flex · height 100% · overflow-x auto · scrollbar-width none
                 scroll-snap-type: x mandatory · touch-action: pan-x pan-y
                 overscroll-behavior-x: contain
.hf-stage-face   flex: 0 0 100% · scroll-snap-align: start
.hf-stage-dots   absolute, bottom 10, centered · two 6px dots, gap 6
```

- **Faces in order: photos, then today.** Photos are the resting face.
  `touch-action: pan-x pan-y` on the track means a horizontal drag flips
  faces and a vertical drag scrolls the page — the stage never traps the
  page-two gesture. `x mandatory` is correct here (each face is exactly one
  stage wide).
- **The photo face** is the existing
  [PhotoCarousel](../../src/Aerie.Web/apps/dashboard/src/components/PhotoCarousel.tsx)
  with its card chrome ceded to the stage (radius/border come off `.hf-photo`
  when staged). Tap-to-advance stays; browsers suppress the click after a
  drag, so swipe and tap coexist. The carousel keeps running while the agenda
  face is shown — pausing it is more machinery than the one preloaded image
  it saves.
- **The agenda face** is a glance, not a scroller — it does not scroll
  internally (nested y-scroll inside an x-snap inside a y-snap is gesture
  soup). Contents, driven by a pure `lib/agendaGlance.ts`:
  - Header: `.hf-sec-head` reading "Today".
  - Up to `AGENDA_GLANCE_MAX = 4` rows in the existing calendar-row idiom
    (time gutter, tinted block, in-progress ring): in-progress and upcoming
    events first, chronological; if fewer than 4 remain, backfill with the
    most recent past events at the existing `.past` fade. More than fit →
    final Meta-register line `+N more today`, aligned with the titles.
  - No now-line here — the in-progress ring carries "right now" at glance
    range; the full treatment lives on page two.
  - Footer whisper when tomorrow has events: `Tomorrow · 3 events · first at
    8:30`, Meta register, muted, clear of the dots (padding-bottom 26).
  - Today empty but tomorrow not → body reads "Nothing today" (muted) above
    the whisper.
- **Existence matrix** (pure `lib/stageFaces.ts`, unit-tested): photos and
  agenda both present → two faces + dots. Only one present → that face alone,
  no dots, no track scroll. Neither → **no stage at all** (today's exact
  renders-nothing behavior, composed).
- **Dots** double as tap targets to switch faces. Active-face state comes
  from the track's scroll position; over the photo face the dots render
  white-on-scrim (the caption scrim already owns that corner of the photo),
  over the agenda face they use `--muted`/`--ink`.
- **Idle reset**: an effect on `resetToken` snaps the track to face 0
  instantly (`scrollTo`, no smooth) — deliberately *not* a remount, which
  would cut a photo mid-dissolve and make the wall look like it is doing
  something on its own.

**Fold budget** — the working numbers Phase 0 confirms or amends, at the two
plausible wall viewports (CSS px):

| | 533×853 (800px panel @1.5×) | 600×960 |
|---|---|---|
| Page padding (top+bottom) | 48 | 48 |
| Header | ~96 | ~100 |
| Climate card | ~360 | ~372 |
| Stage (3:2 of column) | ~293 | ~371 |
| Gaps + hint | ~40 | ~40 |
| **Total** | **~837 ✓** | **~931 ✓** |

Levers if Phase 0 measures tighter, in order: chart height 110→92; stage
aspect 3:2→16:10; the Clock clamp's floor. If it measures *much* taller
(≥1150), nothing changes — air is not a defect. Alerts add on top on hazard
days and page one is `min-height`, not `height`, so overflow scrolls before
the snap point; a hazard day is allowed to push the stage down.

### Page two

Order, every section under an `.hf-sec-head`, each rendering nothing when
empty: **Routines** · **Cameras** · **Panels** · **Lists** (Gather) · **More
rooms** (unpinned ZoneCards) · ~~**Agenda**~~ *(the agenda moved to page one
— see the amendment)*. Tap-density descends; pure reading came last. The tile grids keep their shared-geometry CSS; the only change they take
is the token sweep (gap 18, Name-sm) and their new headers. Gather keeps its
own data path and stays outside the snapshot ternary, exactly as in App.tsx
today. Page two's top gets 18px breathing room below the snap edge.

The skeleton ([DashboardSkeleton](../../src/Aerie.Web/apps/dashboard/src/components/DashboardSkeleton.tsx))
is redrawn to mirror page one only: header is real, then a climate-card block
and a photo-frame block (a stage block, before the amendment). Page two needs no skeleton — it is below the fold by
definition.

### Ledgers

**Z-index** (unchanged, now written down): page content auto · Gather/panel
overlays 20 · health dot 25 · health overlay 26 · circadian veil 30 · camera
overlay 30 (after the veil in DOM order, deliberately — a motion event
outranks a lighting effect). The pager and stage introduce no fixed layers.

**Touch-action**: `html/body: pan-y` · stage track `pan-x pan-y` · panel
steppers `none` · overlays `pan-y`. Nothing else declares one.

**Motion**: photo dissolve (existing) · stage face snap (user-driven) · page
snap (user-driven) · hint's smooth scroll (`auto` under
`prefers-reduced-motion`) · everything else static.

## The mock-first data contract

What [types.ts](../../src/Aerie.Web/apps/dashboard/src/types.ts) gains in
Phase 2, mirrored into
[mockDataSource](../../src/Aerie.Web/apps/dashboard/src/mock/mockDataSource.ts)
and the test source. The API source maps *absent* fields to these defaults,
so the real API needs no lockstep deploy and the follow-up work can land
whenever it lands:

```ts
/** WMO-ish condition vocabulary, small on purpose - one glyph each. */
export type OutlookCondition =
  'clear' | 'partlyCloudy' | 'cloudy' | 'fog' | 'drizzle' | 'rain' | 'snow' | 'storm';

/** One day's shape, for the outside pane's Today/Tomorrow cells. */
export interface DayOutlook {
  highF: number;
  lowF: number;
  condition: OutlookCondition;
}

export interface OutsideAirQuality {
  usAqi: number;
  /** Mirrors the AirQualityBands.cs enum name for name. */
  band: 'Good' | 'Moderate' | 'UnhealthyForSensitiveGroups' | 'Unhealthy' | 'VeryUnhealthy' | 'Hazardous';
  /** ISO 8601. The pill mutes past 2h and the client trusts this, not itself. */
  asOf: string;
}

interface OutsideClimate {
  // ... existing fields; forecast/hourly stay as they are (mock fills them,
  // API returns [] until the follow-up lands) ...
  todayOutlook: DayOutlook | null;     // absent from API -> null -> cell absent
  tomorrowOutlook: DayOutlook | null;
  airQuality: OutsideAirQuality | null; // null -> pill absent
}

interface ZoneClimate {
  // ... existing fields ...
  /** The admin's "lead on the wall" flag. Absent from API -> false. */
  pinned: boolean;
}
```

Mock coverage requirements: at least one snapshot shape with two pinned zones
and two unpinned; a `null` outlook and a `null` AQI variant; an AQI in each
color band reachable; the test source renders the new strings as all-X and
the numbers as 9999 per its rule (condition and band stay real values — they
are enums on the wire, the severity-field precedent).

As built, the knobs are URL params beside `?source=mock`: `mock-aqi=<index>`
for any band, `mock-aqi=stale` for a muted pill, `mock-aqi=none` for no pill,
`mock-outlook=none` for no cells. The mock grew a fourth zone (Sunroom) so
the pinned partition shows two tabs *and* two "More rooms" rows, and the
stale zone is one of the pinned ones on purpose — the dimmed-tab treatment
has to be visible without unplugging anything.


## Phase 0 — Measure the wall

The fold budget above is arithmetic on an assumed viewport. Make it measured,
the way [Degrading gracefully](../kiosk-architecture.md#why-the-fallback-screen-is-not-paranoia) asked the logs
which white screen it was.

- [x] ~~Add viewport fields to the boot log line~~ — **not needed, the data
      was already flowing**: `clientLogger` attaches a metadata blob with
      `screen`, `viewport`, and `devicePixelRatio` to every line it posts.
      Nothing was deployed for this phase; it was a query.
- [x] Queried `aerie-logs` (2026-08-29, ~40 recent `module evaluated` lines).
- [x] `zoneCount` from the current `Dashboard data refreshed` lines.
- [x] Fold table re-run; **no levers needed** — see below.

**What the logs said (queried 2026-08-29):**

| Device class | Viewport (CSS px) | DPR | Orientation |
|---|---|---|---|
| Wall tablet ×2 (GeckoView, `Linux armv81`) | **800×1280** | 1.5 | portrait-primary |
| Dev browsers ×2 | ~1158×773 | 2 | landscape |
| Phones ×2 | 393×695–793 | 3 | portrait |

- **The walls are 800×1280.** The column clamp (`clamp(480px, 75vw, 960px)`)
  resolves to **600px**, so the stage runs 560×373 at 3:2. Page one's calm-day
  budget lands ≈ 940px against 1280 available — ~340px of slack, enough that
  an alert day still fits and the design gets air instead of levers. Chart
  height stays 110, stage stays 3:2, the Clock clamp stays as insurance for
  the phone-sized incidental viewers the logs also show.
- **`zoneCount` is 2.** Outside + 2 zone tabs in the climate card, and the
  no-pins fallback ("first two lead") makes the real wall's day-one render
  identical to the designed one before the admin flag exists.
- Page two starts small: the counts on the wall today are single-digit per
  section, so `proximity` snap is comfortable and revisiting `mandatory`
  stays a Phase 6 feel call.

## Phase 1 — The token pass (no layout change)

Pure consistency: after this phase the page *composition* is identical and
every value is drawn from the tables above. Reviewable as a diff of numbers.

- [x] State the radius tokens in `:root`; migrate every `border-radius` in
      [theme.css](../../src/Aerie.Web/apps/dashboard/src/theme.css) to one
      (mapping table in [the token pass](#the-token-pass-phase-1-makes-this-real)).
- [x] Migrate every font size/weight to a register; kill the strays (16→17
      tile names, 24→22 pick counts, 16→15 meta, badge pill 30px→999px).
      Two deliberate stay-behinds, commented in the CSS: the chart axis sits
      at Micro 11 rather than Label 12 (five stamps share one row), and the
      panel overlay's 68px setpoint is sized against its 84px steppers, not
      against the page.
- [x] Normalize gaps and paddings to the 6-base scale. (The Extreme alert's
      heavier padding survives as the ladder's top rung, commented.)
- [x] Extract `.hf-sec-head` from the agenda's header pair and re-point the
      agenda at it. (Its page-two consumers arrive in Phase 5.)
- [x] Delete the inline styles from OutsideCard and App.tsx's error note in
      favor of real classes; replace the `☀` stat with a plain "sun left"
      stat matching its siblings. (Data-driven inline styles — swatch colors,
      `--cal-color`, list tints, `--health-level` — are the sanctioned
      pattern and stay.)
- [x] Lint + 166 unit tests + build green. **Owner still to eyeball**
      `?source=mock` and the dev scrubber
      ([dev-theme.html](../../src/Aerie.Web/apps/dashboard/dev-theme.html))
      for regressions at noon, dusk, and 3am.

## Phase 2 — Contract and mocks

- [x] Extend [types.ts](../../src/Aerie.Web/apps/dashboard/src/types.ts) per
      [the contract](#the-mock-first-data-contract), comments carrying the
      null semantics. (One correction against the sketch: the band enum is
      `UnhealthyForSensitiveGroups`, matching AirQualityBands.cs exactly.)
- [x] Mock + test sources produce the coverage matrix above; the API source
      defaults absent fields (`?? null`, `?? false`) so a pre-follow-up API
      renders a wall with no outlook cells, no AQI pill, and first-two-lead
      zones — degraded exactly as designed, never broken.
- [x] Unit tests: the absent-field defaults
      ([snapshotDefaults.test.ts](../../src/Aerie.Web/apps/dashboard/src/lib/snapshotDefaults.test.ts))
      and the mock's index→band mirror at every EPA boundary
      ([mockDataSource.test.ts](../../src/Aerie.Web/apps/dashboard/src/mock/mockDataSource.test.ts)).

## Phase 3 — The climate card

- [x] `lib/leadZones.ts`: `partitionZones(zones)` → pinned leads (fallback:
      first two when none pinned) + the rest. Unit-tested both ways plus the
      empty case.
- [x] `lib/aqiPresentation.ts`: band → {label text, hue} per the
      severity-precedent mapping. Unit-tested across bands + stale. The pill
      uses short band words ("Sensitive groups"); the EPA's full phrasing
      stays where the server writes it, the bad-air alert title.
- [x] `components/ClimateCard.tsx` per the anatomy sketch: tab row
      (role=tablist, aria-selected), keyed pane, Outside pane (note, stats,
      outlook cells, AQI pill), zone pane, all three staleness states in both
      tabs and panes. The zone reading (status row, chart, low/high footer)
      extracted to `components/ZoneReadingBody.tsx`, shared with ZoneCard so
      the pane and the "More rooms" rows cannot drift.
- [x] App.tsx: climate card replaces OutsideCard + page-one ZoneCards, keyed
      on `resetToken`; selection falls back to Outside when a poll drops the
      zone. ZoneCard itself stays; unpinned zones render under the climate
      card as today's rows until Phase 5.
- [x] Delete OutsideCard once nothing imports it. (The dev scrubber was the
      last importer; it previews the ClimateCard now.)
- [x] Lint + 181 unit tests + build green. **Owner still to eyeball** the
      mock variants (stale zone, none zone, `mock-outlook=none`,
      `mock-aqi=none|stale|<index>` per band).

## Phase 4 — The stage

- [x] `lib/stageFaces.ts` (existence matrix) and `lib/agendaGlance.ts`
      (row selection, backfill, overflow count, tomorrow whisper) — pure,
      unit-tested at the boundaries. One wording call made in code: the
      whisper's time uses `formatShortTime`'s prose form ("first at 8:00am"),
      not the gutter's "8a" shorthand — it is a sentence, not a column.
- [x] `components/Stage.tsx`: track/faces/dots per spec; scroll-position →
      active dot; dot taps (smooth unless reduced-motion); `resetToken`
      effect snapping to face 0 without a remount.
- [x] Photo face: PhotoCarousel cedes card chrome to the stage via a wrapper
      rule (`.hf-stage-face .hf-photo`); behavior otherwise untouched.
- [x] Agenda face per spec, reusing the calendar row CSS — `EventBlock` is
      exported from CalendarSection rather than copied, so the glance and the
      full agenda render the same object.
- [x] App.tsx: stage replaces the bare PhotoCarousel; CalendarSection stays
      where it is until Phase 5 moves it (today intentionally appears in both
      until then).
- [x] Lint + 196 unit tests + build green. **Owner still to check**
      swipe-vs-tap feel on hardware when convenient.

## Phase 5 — Two pages

- [x] Restructure App.tsx into `.hf-p1` / `.hf-p2`; snap CSS on `html`;
      `100dvh` with the `100vh` fallback line. Vertical padding moved from
      `.hfdev` into the pages, so page one's padding lives inside its 100dvh
      and the fold math holds.
- [x] Page two sections in order with `.hf-sec-head` headers: Routines,
      Cameras, Panels, Lists, More rooms (unpinned ZoneCards), Agenda. One
      deviation, commented in App.tsx: the agenda carries no "Agenda" header —
      its own day labels are this idiom's original home, and an AGENDA label
      directly above a TODAY label is a stutter.
- [x] The `hasPageTwo` guard and the swipe hint (tap → `scrollIntoView`,
      reduced-motion honored). Gather counts toward page two on its own,
      because it rides its own data path.
- [x] Redraw DashboardSkeleton as page one's shapes (climate card + stage;
      the orphaned `.hf-out` rules left with it).
- [x] Lifecycle verified in code: the reset's `scrollTo(0,0)` is page one's
      snap point; presence listens to touches, which every swipe starts with;
      overlays and the veil are `position: fixed` and cover both pages.
      **Owner confirms the same on hardware in Phase 6.**
- [x] Lint + 196 unit tests + build green.

## Phase 6 — The hardware pass

The owner drives; findings land as fix commits, and this phase closes only on
their sign-off. The checklist to walk, on a wall tablet and the dev scrubber:

- [ ] Fold: calm day fits page one at every measured viewport; alert day
      pushes acceptably.
- [ ] Circadian sweep: noon, golden hour, the dusk flip, 3am — no lamp, no
      contrast loss, AQI/severity hues legible at each.
- [ ] Gestures: page swipe vs stage swipe vs photo tap vs zone tabs don't
      cross; snap feel (decide `proximity` vs `mandatory` here).
- [ ] Degradation: `?source=mock` staleness variants, a killed API mid-run
      (health dot + stale panes), no-photos house, no-calendar house.
- [ ] Idle reset from every state: page two, agenda face, a zone tab, an
      open overlay.

## Follow-up: the data contract to fulfill

**Not part of this plan.** The wall renders the design from the mock and
degrades gracefully against today's API. This section is the spec the API
work opens from — small enough to land as one plan or three commits.

1. **Outside forecast + outlooks.** A forecast provider on the pattern of
   [OpenMeteoAirQualityProvider.cs](../../src/Aerie.Api/Services/Hazards/OpenMeteoAirQualityProvider.cs):
   Open-Meteo's keyless forecast endpoint, site lat/lon, requesting hourly
   `temperature_2m,relative_humidity_2m,cloud_cover,precipitation` and daily
   `temperature_2m_max,temperature_2m_min,weather_code` for today+tomorrow.
   Fills `OutsideClimate.Forecast` (joined at "now", replacing nothing —
   [ForecastService](../../src/Aerie.Api/Services/Dashboard/ForecastService.cs)'s
   damped extrapolation remains the *zones'* projector), `Hourly`,
   `TodayOutlook`, `TomorrowOutlook`. WMO `weather_code` → `OutlookCondition`
   mapping: 0–1 clear · 2 partlyCloudy · 3 cloudy · 45–48 fog · 51–57
   drizzle · 61–67, 80–82 rain · 71–77, 85–86 snow · 95–99 storm. Cached
   ~30min; on failure everything stays null/empty and the dashboard never
   blocks (the `WeatherService` best-effort precedent, timeout included).
2. **AQI into the snapshot.** The hazard sync already holds an
   `AirQualityReading`; surface `Current` (`usAqi`, band word via
   [AirQualityBands.cs](../../src/Aerie.Api/Services/Hazards/AirQualityBands.cs),
   `asOf`) through `DashboardService` as `OutsideClimate.AirQuality`. Null
   when the provider is unconfigured or the reading is stale server-side.
   The bad-air *alert* path is untouched — pill and alert are different
   statements and both belong.
3. **The `pinned` flag.** A boolean on the zone row `ZoneService` reads
   (surfaced as `ZoneClimate.Pinned`), plus the admin Zones page toggle —
   "Lead on the wall" — next to the existing ordering. Server keeps
   `SortOrder` ordering; the client partitions. Mirror the field into
   [dashboard-api-manifest.md](../dashboard-api-manifest.md) with the rest.
4. When all three land, delete the API-source default-mapping comments from
   Phase 2 and the mock-first caveat from this plan's status line — or
   dissipate the whole plan per the
   [lifecycle](README.md#the-lifecycle) if it is done by then.

## Verification

`make test-web` per phase (lint + unit + build, per app, via `make` — not
`dotnet`/npm directly). No API tests until the follow-up. Visual verification
is the owner's, per the split that already governs this repo: Claude builds,
lints, and unit-tests the pure logic (`agendaGlance`, `stageFaces`,
`leadZones`, `aqiPresentation` carry the behavior worth asserting); the owner
eyeballs `?source=mock`, the dev scrubber, and the hardware.
