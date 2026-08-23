# What Aerie Is For

## Summary

[`ethos.md`](ethos.md) is the constraint: *nothing in this repo may be true of
exactly one installation.* It answers **how** to build Aerie. This document
answers **why** there is anything to build.

> **Aerie exists to clear technical moats out of the way of things that used to
> be sources of joy.**

Not to automate the house. Not to self-host for its own sake, or to save money on
subscriptions, though it does both. Those are consequences. The pillar is that
there are things a person wants to do — take photographs, cook from the recipes
their family actually uses, find the thing in the box in the garage, sit down and
watch something together — and that between the wanting and the doing there is
now, routinely, a **moat**: a layer of file management, account sprawl, format
migration, subscription churn, and vendor lock-in thick enough that the wanting
quietly stops.

The moat is not a minor tax. It is load-bearing enough to end hobbies.

## The founding case

The clearest instance, and the one that named the pillar:

A photographer stops taking photographs. Not because the camera got worse or the
light got worse or the interest went away — because every shoot means a card full
of large RAW files that need somewhere to live, a catalog that only one
proprietary application can read, a cull that never happens, an edit that is
three tools deep, and a share step that depends on a service that will change its
terms next year. The photographs are still worth taking. The **file management**
is what stopped it.

That is a technical moat around a source of joy. It is exactly the kind of
problem a home cloud is well-shaped to drain, and draining it is worth more than
any individual feature this repo will ever ship.

## What this implies about how to build

The pillar earns its place here only if it changes decisions. It changes four.

### 1. Own the data, in a format that outlives the tool

A moat is usually made of *lock-in*, so the countermeasure is that the durable
artifact is always a file on a filesystem or a row in a Postgres database this
household controls — never a record inside a vendor's account, and never a
proprietary sidecar catalog only one application can read.

The test: **if the application that manages this data disappeared tomorrow, what
would still be readable?** If the answer is "nothing useful", the moat has been
rebuilt one layer down.

### 2. Integration is the product; the individual app is not

Moats form in the **gaps between** tools, far more than inside any one of them.
The photograph that is in the camera, then in a folder, then in an editor's
catalog, then in a cloud album, then in a text message, has crossed four
boundaries — and every one of them is a place where metadata is dropped, a copy
diverges, and the file becomes something to manage rather than something to
enjoy.

This is the actual argument behind
[`family-apps-architecture.md`](family-apps-architecture.md)'s modular monolith
and its one shared database. "Apps can read each other's data without an
integration project" is not an engineering nicety. It is the mechanism by which
the gaps stop being moats.

### 3. Deferring a decision is fine; foreclosing one is not

Aerie is built incrementally, and most things should be. But there is a
difference between *not building* something and building something that makes it
expensive to build later — and the difference matters most for the long-horizon
goals, because those are exactly the ones where an impure bit of forethought is
cheap and a reopened decision is not.

The practice: when a plan names a future direction, it also names **what would
close the door** on it, and checks that nothing in the current phase does. The
photography section of [`plans/immich.md`](plans/immich.md) is the worked
example — no code, one design constraint, and an explicit list of the decisions
that keep the pathway open.

This is a deliberate deviation from strict agile practice, and it is worth being
honest that it is one. It applies to **certain** long-term goals, not to
speculative ones.

### 4. The finished state is "I don't think about it"

The measure of a moat being drained is not that the tooling is good. It is that
the tooling stopped being visible. Nobody thinks about the fact that a light
switch works.

So a feature is not done when it functions. It is done when the thing it enables
happens **without the household planning around it** — when photos back up
because phones back up, when the recipe is on the tablet because it is dinner
time, when the file is findable because it was never lost. Anything that requires
a person to remember a procedure is a moat with better documentation.

## The relationship to `ethos.md`

They are different documents on purpose, and both are constraints.

`ethos.md` is about **redeployability**: Aerie is a product that produces
deployments, and no operator's values may be baked into the artifact. That is a
constraint on how things are written.

This document is about **motive**: what is worth writing at all, and which
trade-offs are the right ones when two designs are equally clean. Where they
interact, they reinforce each other — a household that owns its data, in open
formats, on its own hardware, is the same household that can hand the whole
system to someone else and have it work.

Where they might appear to conflict — a feature that would be joyful here but
meaningless to a second operator — `ethos.md` wins on *implementation* (it
becomes a parameter, not a hardcoded fact) and this document wins on *whether to
build it at all*.

## Applying this to a change

Alongside [`ethos.md`'s four questions](ethos.md#applying-this-to-a-change):

1. **What did this make easier that a person actually wanted to do?** If the
   honest answer is "nothing yet, but it's infrastructure for something", that is
   a fine answer — name the thing.
2. **Does it drain a moat, or move one?** Replacing a vendor's lock-in with a
   local application's lock-in is moving it.
3. **Will it still work when the vendor, the format, or the subscription
   changes?** If not, what is the durable artifact underneath it?
4. **Does it require anyone to remember a procedure?** If yes, that is the part
   still unfinished.
