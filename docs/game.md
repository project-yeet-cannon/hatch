# Game

## Summary

A game a child builds by saying what should happen. The player types "i am a red
ball", waits, and a red ball appears that they can steer. They type "i want to
roll down a hill", and there is a hill. Every request rewrites the running game,
live, with no rebuild and no deploy — the code arrives from Claude, is stored as
a new version, and is swapped into a sandboxed frame that is already on screen.

It is the third app on the family-apps platform
([`family-apps-architecture.md`](family-apps-architecture.md)): one folder under
`src/Aerie.Api/Modules/`, one folder under the shell's `src/modules/`, one line
in each registry, its own `game` Postgres schema. No container, ingress, CI job
or manifest was touched to add it.

Spelled `Game` / `game` throughout — C# namespace `Aerie.Api.Modules.Game`,
Postgres schema `game`, routes under `/api/game`, shell module id `game` at
`/apps/family/game/`.

Four decisions carry the whole design:

- **A thin engine the model writes against**, rather than the model writing a
  game from scratch each turn. See [The engine](#the-engine).
- **A sandboxed frame with an opaque origin**, so code nobody reviewed cannot
  reach the house. See [The sandbox](#the-sandbox).
- **Every version kept, and going back is one tap.** The author is a language
  model and the player is four; something will eventually come back worse than
  what it replaced. See [Versions](#versions-and-going-back).
- **Crashes repair themselves, twice, then undo.** See
  [When a game breaks](#when-a-game-breaks).

## The loop

```
  type ──▶ POST /api/game/worlds/{id}/turns
              │
              │  system prompt: the engine reference (cached, 1h)
              │  user message:  current code + recent prompts + the request
              ▼
           Claude ──▶ <summary> <extra> <code>
              │
              ▼
       new game.Versions row, world.CurrentVersionId points at it
              │
              ▼
   postMessage ──▶ sandboxed frame ──▶ new Function(code) ──▶ playing
```

Nothing in that path touches CI, the filesystem, or a container. A turn is a
database row and a `postMessage`.

**The game keeps running while a turn is being written.** The frame is never
blanked or reloaded on submit — a thirty-second wait staring at a frozen screen
is the difference between this being fun and being a chore. The swap happens
when the code arrives.

## The engine

`src/Aerie.Web/apps/family/src/modules/game/runtime/engine.js` is a small 2D
engine: a loop, gravity, a ground profile, collisions, keyboard and touch
control, a camera, sound, and a records system. Generated code never writes any
of that. It writes `defineGame({ setup, update })` against a world object:

```js
defineGame({
  setup(w) {
    w.sky('#7ec8f5', '#dff3ff');
    w.hill(0, 520, 900, 200);
    const ball = w.ball({ x: -200, y: 400, r: 26, color: '#e53935' });
    w.control(ball, { speed: 340, jump: 760 });
    w.follow(ball);
    ball.onLand((jump) => w.record('longest-jump', 'Longest jump', jump.distance, { unit: 'm' }));
  },
});
```

**Why an engine at all.** Everything in it is the part that is identical in
every game a small child asks for. Holding it still turns a turn into fifty
lines of intent instead of six hundred lines of boilerplate, which is the
difference between a change landing in twenty seconds and landing in two minutes
with a new bug in the physics. It also bounds what can go wrong: the failure
modes of `w.hill(...)` are known, where the failure modes of a hand-rolled
collision routine are not.

**The ground is one left-to-right profile.** `w.ground`, `w.hill` and
`w.terrain` all append to a single polyline, so "what is under this ball at x"
has exactly one answer and rolling, slope acceleration and landing detection are
all a lookup. Platforms, ceilings and floating islands are static bodies
instead. The alternative — a soup of overlapping surfaces — turns that lookup
into an ambiguous search whose failure mode is a ball falling through a hill it
was sitting on.

**Jumps are measured by the engine, not by the model.** A body carries
`lastJump` (`distance`, `height`, `airtime`, filled in on landing) and flights
under 0.25s are not jumps. That exists so "longest jump" is a value the model
reads rather than a mechanism it reinvents and gets subtly wrong — and without
the airtime floor, every game's longest-jump record is set in the first second
by a ball settling onto the ground.

### The reference is the interface

`Modules/Game/GameEngineReference.cs` describes that API in prose. It is the
system prompt, and it is the only thing the model knows about the engine.

A prompt is an interface, and it is one nothing type-checks: rename a method in
`engine.js` and nothing goes red — the next game simply calls something that no
longer exists, and the first anyone hears about it is a crash on a tablet.
`GameEngineReferenceTests` closes that gap. It parses the `world` object out of
`engine.js` and fails the build if the engine exposes anything the prompt never
mentions, or if the prompt promises anything the engine does not have. It is the
only test in the repo that reads the web app's source, and that is what it is
for.

The other direction is covered from the JavaScript side. `engine.test.js` runs
the engine headlessly (`harness.js` stands in for `window` and the canvas) and
asserts the behaviour the prompt describes in words: a ball settles on flat
ground, a ball on a hill rolls down it, a record keeps only the best, a rule
that throws does not stop the game. Two of those tests read the **worked example
from `GameEngineReference.cs` and the seed code from `GameService.cs`** out of
the C# and run them. A copy of either in the test file would pass forever while
the original rotted — and the example is what every turn imitates, so an example
that does not run is a bad turn every time.

## The sandbox

The frame is created with `sandbox="allow-scripts"` and nothing else. Without
`allow-same-origin` it has an **opaque origin**: no cookies, no same-origin
fetch, no `localStorage`, no access to the shell around it. Generated code
cannot call Aerie's API even by accident. Adding `allow-same-origin` back would
quietly undo all of it.

Three consequences follow, and each explains a piece of the runtime:

- **The engine and host are inlined into the frame document as text** (`?raw`
  imports, see `frame.ts`). A `<script type="module">` would be a cross-origin
  fetch that CORS refuses, and `import()` of a `blob:` URL minted in an opaque
  origin is refused outright.
- **Code is evaluated with `new Function`**, which is the plainest dynamic
  evaluation that works there — and is exactly the "live code, no rebuild"
  mechanism the app exists for.
- **Records are persisted by the parent.** An opaque origin has no storage of
  its own, so the frame posts new personal bests up and the shell `PUT`s them.

Everything else crosses by `postMessage`: `load` / `pause` / `resume` down,
`hello` / `ready` / `error` / `records` up. The parent checks
`event.source === iframe.contentWindow`; the frame checks `event.source ===
parent`.

## Versions and going back

```
game.Worlds    id, name, icon, current_version_id, records_json,
               created_at, updated_at
game.Versions  id, world_id → Worlds (CASCADE), ordinal, kind, prompt, code,
               summary, extra, model, parent_version_id, created_at,
               duration_ms, input_tokens, output_tokens, cached_input_tokens,
               broken_at, broken_error
               unique (world_id, ordinal)
```

A world *is* its history; the code it runs today is whichever version
`current_version_id` names. `kind` is `Seed`, `Turn`, `Repair` or `Revert`.

**Going back writes a new version holding the old code** rather than moving the
pointer backwards. It costs a row and buys two things: undoing an undo is just
another step, and the history stays a record of what was played rather than a
tree of what might have been.

**Records live on the world, not the version**, because a personal best belongs
to the child rather than to a build — rewriting the game does not un-jump the
longest jump. They are stored as the JSON the engine hands up, uninterpreted, so
the day a game invents "highest bounce" nothing server-side needs changing.
Unreadable JSON degrades to "no records yet": a lost best is a shame, an
unopenable game is the end of the afternoon.

**Token counts are stored per version** and shown in the history panel. A family
running this on their own API key should be able to see that a careful turn
costs more than a quick one without going looking for it.

## When a game breaks

The frame reports the first exception from any phase — `compile`, `setup`,
`update`, `draw`, plus `window.onerror` and unhandled rejections — and **stops
its loop**. Left running it would repaint the same exception sixty times a
second, flooding the repair with duplicates and pinning the tablet while a child
watches a frozen screen.

The shell then:

1. **Repairs**, up to twice: the crash and the code go back to the model, which
   is told to fix it and to say nothing about the crash in its summary. The
   child does not need to know anything went wrong.
2. **Falls back**: marks the version broken and undoes to the newest version
   that still works, saying "That one didn't work, so I put the game back."

Two attempts, not three: the value of a third try is small and its cost is not —
another half-minute of stopped game in front of someone who cannot read the
reason — while going back is instant and always works.

Two guards matter here. A broken version is **never reverted to and never used
as the basis of a new turn**; a turn after a crash builds on the last version
that worked, because handing the model code that throws is how one bad turn
becomes every following turn. And a crash report naming a version that is no
longer live — a frame left open in another tab — is **ignored**, rather than
overwriting what the person actually playing is looking at.

## Talking to Claude

`GameAuthor` is the only place a model id is written down. The client sends a
*speed*, not a model:

| Choice    | Model             | Effort   | For                                        |
| --------- | ----------------- | -------- | ------------------------------------------ |
| `Quick`   | `claude-sonnet-5` | `medium` | The default, and nearly every turn         |
| `Careful` | `claude-opus-5`   | `high`   | The ask that keeps coming back wrong       |

Repairs are always `Quick`: the child is already staring at a stopped game and
the fix is nearly always small.

**Answers are delimited, not JSON.** A whole source file inside a JSON string
means every newline and quote in the game is escaped — tokens spent on the most
expensive part of the response, plus a class of failure (one bad escape) that
loses the entire turn. `<summary>`, `<extra>` and `<code>` have neither problem.
`GameAnswerParser` is deliberately tolerant: a missing closing tag, a markdown
fence wrapped around code that was already inside tags, prose before the first
tag. All of those happen, and none is worth losing a turn someone is waiting on.

**The call streams, and the stream is drained server-side.** Streaming is what
keeps a 32k-token generation from dying on an HTTP timeout. Forwarding it to the
browser would mean an SSE contract and a partial-code state on the client for no
gain — a half-written game is not playable, so there is no progress worth
rendering token by token.

**The engine reference is cached with a one-hour TTL.** It is most of every
request and byte-identical across every turn in the house, so an afternoon of
play reads it from cache. This is also why the repair instruction is a *user*
message rather than an edit to the system prompt: changing the prompt for
repairs would miss the cache on exactly the turns where a child is already
waiting twice. A run of zeroes in `cached_input_tokens` is the symptom of that
having been broken.

## Gamification

The model is told to weave in one small mechanic per turn without being asked
and to name it in `<extra>`, which the shell shows as a gold line under the
summary. Adversity is the part people cannot think up for themselves.

`w.record(id, label, value, opts)` is the cheapest version of it: the engine
keeps the best, announces it on screen when it is beaten, and it persists between
visits. The first value of a new record is never announced — that is not a
personal best, it is just the first one.

One idea per turn, deliberately. A game that grows a mechanic per turn stays
legible; a game that grows four becomes noise nobody can steer.

## Setup

The module needs an Anthropic API key, which is operator-supplied like the
Google calendar credentials — it is billed to whoever runs the house, so it is
never shipped ([`ethos.md`](ethos.md)).

- **Deployed:** admin app → Settings → **Anthropic API key**. Stored obfuscated
  and redacted on read, the same as the Home Assistant token.
- **Local dev:** `anthropic_api_key` in `src/Aerie.Api/.env.json`.

With no key set, `GET /api/game/capability` reports it and the play screen says
so instead of offering a text box — an unconfigured install fails on the way in
rather than after the first thing a child types.

## Deferred

- **No voice input.** Typing is a hard gate for a four-year-old, and today the
  parent types. Speech is the obvious next thing and needs no server change: the
  browser's `SpeechRecognition` fills the same box.
- **No 3D.** The engine is 2D. "Roll down a grassy hill" reads well in side view;
  a first-person world would be a different engine, not a bigger one.
- **No sharing between houses.** A game is rows in one database. Exporting one
  is a code blob and a records blob, and nothing yet asks for it.
- **No per-person anything.** The wall authenticates a device, not a person
  ([`auth-architecture.md`](auth-architecture.md)), so records belong to the
  world rather than to a player.
- **No cost ceiling.** Token counts are recorded and shown, but nothing enforces
  a budget. If that becomes a worry, the counts are already there to enforce
  against.
