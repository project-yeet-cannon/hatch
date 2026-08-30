# Quill

## Summary

Private notes, one person's own, readable on a phone with no connectivity at
all. Start typing, and it saves itself.

It is the fifth app on the family-apps platform described in
[`family-apps-architecture.md`](family-apps-architecture.md), and the first one
that is not the household's. Storage, Gather and Game all hold facts about the
house — a crate is in the garage whoever opens the app. A note belongs to the
person who wrote it, and that single difference is what everything below is
about.

The name is the product's own joke: an aerie is a nest, a quill is a feather,
and a quill is also what you write with. It is spelled `Quill` / `quill`
everywhere — C# namespace `Aerie.Api.Modules.Quill`, Postgres schema `quill`,
routes under `/api/quill`, shell module id `quill` at `/apps/family/quill/`.

Three properties carry the design:

- **A note belongs to a person.** Not to a device, and not to the house.
  `PersonId` is the first clause of every query in the module, not a stamp
  recording who wrote it.
- **Every refusal is the same blank 404.** No person on this device, someone
  else's note, and a note that does not exist are indistinguishable from
  outside.
- **Offline is a first-class read.** The shell mirrors every note this person
  has onto the device. Writing them back is deliberately not attempted.

## The first module that reads a person

Four modules never had to. A crate is in the garage whoever opens the app, so
`Modules/README.md` and [`auth-architecture.md`](auth-architecture.md) could
both say that nothing branched on a person — and while that was true it was
worth saying, because it is what kept a person a name on a grant rather than an
account with a permission model growing quietly behind it.

It is not true any more, and that is the intended direction rather than a
concession. A person identifies an individual; who someone is belongs in an
authorization decision. Ownership is the first and simplest form of that, and
[sharing](#deferred) — this note, that person, read or write — is the next, with
nothing in the current shape needing to change for it.

What Quill does *not* do is invent a permission model on the way past. There is
still no role, no scope, no permission table, and nothing reads
`Person.IsAdmin`; a household-wide administrator is the coarsest possible answer
to a question nobody has asked, and the deploy that first enforces one is the
deploy that can lock everyone out
([Whose device is this](auth-architecture.md#whose-device-is-this)). Ownership
needs none of that: it is a `WHERE` clause, in one place, that no code path can
forget to consult.

The seam that made it one line rather than a refactor is
[`ICallerIdentity`](../src/Aerie.Api/Services/Auth/CallerIdentity.cs): "who is
calling", resolved once per request. It hides the cookie (so a second way to
identify a caller cannot drift into existence) and it hides `Auth:Enabled` (so a
person-scoped module is not dark on a developer's machine, where the wall is off
but the browser's cookie is fine). `AuthController` had carried that fallback as
a private method since the wall landed; the second asker is what turned it into
a seam.

## Data model

One table in the `quill` schema —
[`Entities.cs`](../src/Aerie.Api/Modules/Quill/Entities.cs).

```
quill.Notes  id, person_id, title, body, created_at, updated_at
             index (person_id, updated_at)
```

`person_id` is **not** a foreign key, matching `AuthInvites.PersonId` and for a
related reason: `People` lives in the core `public` schema and this table lives
in `quill`, and a cross-schema FK from a module into the platform is exactly the
compile-time coupling [`Modules/README.md`](../src/Aerie.Api/Modules/README.md)
forbids. The consequence is chosen rather than tolerated: deleting a person
leaves their notes unreadable rather than deleting them, which is the safer of
the two failures — `SetNull` would make them everyone's, and `Cascade` would
make an administrative tidy-up destroy someone's writing.

The index is composite and in that order because the module runs one query:
this person's notes, most recently edited first. `person_id` alone would answer
the filter and leave a sort in front of every read.

**Title is blank, not null, for an untitled note.** That is the ordinary state —
you start typing the body and may never name the thing — so nothing in the write
path treats it as incomplete, and the list renders the placeholder plus the
start of the body.

### Where sharing attaches

A note has one owner and no shared-with list, and `PersonId` stays the owner
when there is one — sharing is a join table beside this one (note, person,
whether they may write), not a second column here. So this shape does not change
when it arrives, and the module's one query grows from `PersonId == me` into
"mine, plus the ones shared with me" in the same single place it lives now.

Until then, "who can read this note" has exactly one answer, which is worth
having while the protection underneath is obfuscation rather than encryption.

## Protection at rest

Title and body are stored through
[`SecretProtector`](../src/Aerie.Api/Common/SecretProtector.cs), the same way a
camera password or a calendar refresh token is: scheme `v1`, which is XOR
obfuscation and is [honestly labelled as
such](secrets-architecture.md#what-deliberately-stays-out). It is not
encryption, it does not defend against someone with the database *and* the
source, and it is not trying to. What it does is mean that a database dump, a
backup on someone else's disk, or a `psql` session opened for an unrelated
reason does not put the household's private writing on screen in plaintext.

The one thing worth copying from this module is *where* the protection lives.
It is a **value converter on the context**
([`QuillContext.cs`](../src/Aerie.Api/Modules/Quill/QuillContext.cs)), not a
`SecretProtector.Protect()` call at each write path. The difference is the
failure mode:

- Protect-at-the-write-path fails **silently**. A new endpoint that forgets
  writes plaintext, and `Unprotect` reads an untagged legacy value as v1 — so
  the row round-trips perfectly, nothing breaks, and nobody finds out.
- As a converter there is no call site to forget. The property is plaintext
  everywhere in the process and ciphertext everywhere in Postgres, including in
  raw SQL in a migration. A test asserts that *every* string property on the
  entity has a converter, which is a test that fails for a column that does not
  exist yet.

What it costs, stated here because it must never be rediscovered the hard way:

> **No SQL predicate over `Title` or `Body` can be correct.** EF translates
> `Where(n => n.Body.Contains(q))` against the stored bytes, which are
> obfuscated. It compiles, it runs, and it matches nothing.

So search is client-side over the notes the shell already holds, and ordering is
by `UpdatedAt`, never by title. If server-side search ever becomes necessary it
needs a searchable projection that is explicitly *not* protected — a decision
with a privacy answer attached, not an index.

The upgrade path is `SecretProtector`'s own: real crypto is a new scheme
registered beside `v1` plus a re-protect on next write, with rows in both
formats readable throughout.

## The wall around a note

Two enforcement points, and they are independent on purpose.

**The API refuses.**
[`QuillController`](../src/Aerie.Api/Modules/Quill/QuillController.cs) resolves
the caller's person once and makes it the first clause of every query. Every
refusal is `404` with an empty body:

| Situation | Answer |
|---|---|
| No grant at all (unenrolled, or the wall is off) | `404` |
| A grant nobody has claimed | `404` |
| A note id belonging to someone else | `404` |
| A note id that does not exist | `404` |

A `403` would confirm that the id names a real note belonging to a real person,
which is the one fact a private note has to keep. The refusal also comes
**before** validation, so a malformed request cannot come back as a `400` and
thereby reveal that a `404` was coming.

**The shell hides.** A module in the family shell's registry may declare
`requiresPerson`, and one that does gets no home-screen card, no tab, and no
route for a session with no person
([`registry.ts`](../src/Aerie.Web/apps/family/src/modules/registry.ts)). Typing
the URL lands on the shell's ordinary "nothing here" — the same answer a stale
bookmark for a deleted module gets, deliberately, because a device with no owner
should not be able to learn that Quill exists.

It is one flag rather than a predicate. The moment it becomes
`canSee: (session) => boolean`, the decision about who may read a module's data
lives in the shell, in a file a module author is invited to edit, instead of
behind the API that actually enforces it. Hiding is courtesy; the braces are the
404.

The shell learns who it is from `GET /api/auth/me`, which already existed.

## Offline

The requirement in one sentence: a note written on Monday must be readable on
Thursday from a plane — and from a house whose internet is down, and from a
phone that has web connectivity but is off the tailnet. All three look identical
to a `fetch`, and none of them should cost someone the thing they wrote.

The shell's service worker already network-first caches `/api` GETs, which
covers part of this for free. It is not enough alone for two reasons: it can
only serve URLs this device happened to request, and it cannot tell the UI that
what it served was old.

So Quill keeps a **mirror**
([`store.ts`](../src/Aerie.Web/apps/family/src/modules/quill/store.ts)):

- **IndexedDB, not `localStorage`.** `localStorage` is synchronous on the main
  thread and capped around 5 MB for the whole origin, which the shell is already
  spending on the device id and the session.
- **Whole notes, not summaries.** This is why `GET /api/quill/notes` returns
  bodies (see the tripwire in
  [`Dtos.cs`](../src/Aerie.Api/Modules/Quill/Dtos.cs)). A mirror filled from
  summaries could only offer the notes someone had happened to open, and the one
  written on Monday and not re-opened is exactly the one wanted on Thursday.
- **Keyed by person.** A device can be re-linked to someone else on the admin
  Sessions page. A mirror that only held notes would hand the previous owner's
  notes to the new one the first time the API was unreachable — the exact
  failure the mirror exists to prevent, arriving through the mirror. A snapshot
  whose person does not match is deleted, not skipped: offline, the "next
  successful sync" that would have cleared it may never come.
- **Read first, then replace.** The mirror is read and rendered before the live
  request resolves, so a cold launch in airplane mode draws notes rather than a
  spinner that becomes an error.
- **Read-only, and it says so.** A screen showing mirrored data shows a line
  saying it is saved on this device, and offers no writes.

Offline *writes* stay out of scope, as they are for every other module: they
need conflict resolution that nothing here justifies. The honest version of that
constraint is on screen rather than in a save button that fails for reasons
nobody can see.

The storage is an interface with an in-memory implementation beside the
IndexedDB one, for two reasons that are both about correctness: the mirror's
rules are the part that can be wrong and are only testable against a store that
is not a browser, and IndexedDB is genuinely absent sometimes — Safari private
browsing has shipped builds that hand you a database and then refuse to write to
it. That failure surfaces at the first transaction rather than at `open()`, so
the fallback triggers there.

## API

Every route is `404` for a caller with no person. All of them are behind the
house wall like everything else.

| Route | Does |
|---|---|
| `GET /api/quill/notes` | Every note this person has, newest edit first, bodies included |
| `GET /api/quill/notes/{id}` | One note — for a link opened cold into a note written on another device |
| `POST /api/quill/notes` | Creates one. Refuses a note with nothing in it |
| `PUT /api/quill/notes/{id}` | Overwrites both fields |
| `DELETE /api/quill/notes/{id}` | Deletes it |

`PUT` may blank a note without deleting it. The editor saves continuously, and a
server that deleted the row the moment someone selected all and started retyping
would be a server that eats notes. Deleting is its own verb.

The title is trimmed; **the body never is**. Leading blank lines and trailing
whitespace are things a person typed into their own note, and a server tidying
them up moves the cursor out from under them on the next sync.

## The editor

Three decisions, all of them from the same want — *I just want to start
writing*.

**It opens on the body.** The title input sits above the body and is scrolled
out of view on a phone; the first keystroke goes into the note rather than into
a field asking what the note is going to be about. Scrolling up finds it. The
mechanism is that the body's `min-height` is the full height of the scroll
container, so there is always exactly enough overflow to have hidden the title
above it. On a screen wide enough for both (≥900px) it does not scroll past,
because hiding a field that fits is a puzzle rather than a focus.

**There is no save button.** A note saves itself 800ms after you stop typing,
and again on the way out of the screen — tapping back, or the OS taking the app,
must not cost the last few seconds. A Save that can be missed is a note that can
be lost.

**A new note is not a row until it has something in it.** `/quill/new` is an
editor with no id behind it; the first save with content creates the note and
*replaces* the URL, so Back leaves the editor rather than returning to the blank
note this one just stopped being. Tapping New and changing your mind leaves
nothing behind.

The list previews an untitled note as `Untitled` plus the start of its body,
rather than promoting the first line to a title. Both are defensible and this
one is honest: a note whose heading is silently its own first line looks named,
so whoever meant to name it never finds out they did not.

## Deferred

Named so they are decisions rather than oversights.

- **Sharing** — the next feature, not a maybe. A join table beside `Notes`
  (note, person, and whether they may write), and "the notes you may read" stops
  being `PersonId == me` and becomes a slightly larger expression *in the same
  one place*. The rules that hold it together are already in force: ownership is
  a clause in the query rather than a check after the load, and every refusal is
  the same blank `404` — a shared note you have lost access to must go back to
  looking like a note that does not exist. Write access is where the module
  first has to answer a question it currently ducks, which is what two people
  editing one note at once means; the answer is likely the same conflict work
  offline writes need, which is why the two are worth designing together.
- **Offline writes.** As above, and as everywhere else in the shell.
- **Real encryption.** A new `SecretProtector` scheme, which the type is built
  for. Worth doing before this holds anything whose exposure would matter more
  than embarrassment.
- **Server-side search.** Impossible against protected columns by construction;
  see [Protection at rest](#protection-at-rest). Client-side search over one
  person's notes is not obviously worse until the count is far past a
  household's.
- **Attachments, formatting, folders.** Plain text, one flat list. Every one of
  these is a real feature and none of them is the thing that was missing.
