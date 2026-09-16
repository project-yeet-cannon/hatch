---
title: How to Hatch
lede: What to expect from a board that agents work overnight. Playbooks, the order work is taken in, and the points where a person is the one who moves things.
permalink: /how-to-hatch/
---

Hatch is a kanban board. If you have run one before, most of it is familiar:
columns, cards with keys like `AER-12`, comments, parents and children. What is
different is who moves the cards. An agent picks up the rightmost ticket it is
allowed to advance, spends one session on it, hands it back, and asks for the
next one. Your job becomes writing good tickets, answering questions, and
merging.

This page is the shape of that arrangement. The mechanics of starting the
agent are on [Running the agent](running-the-agent.md).

## One board, every project

A project is a key namespace, not a container. `HOME` gives you `HOME-1`,
`HOME-2`; a second project gives you `SHOP-1`. Every project shares one board
and one set of columns, because switching boards to find out what is next is
the thing a folder of plan files already did badly.

Issues come in four types: **epic**, **story**, **task** and **bug**. An epic
takes stories, a story takes tasks, and a bug can sit under an epic or a story.
The description is markdown and it is the brief. Everything else, comments,
questions, the event trail, is context.

## The columns, and whose they are

A fresh board ships with seven columns. Read the last column of this table
first: it is the whole model.

| Column | What happens in it | Whose |
|---|---|---|
| Draft | An idea being written. Nothing reads it. | You |
| Breakdown | Turn the draft into a specification: acceptance criteria on the issue, children under it. | Agent |
| Backlog | Specified work, awaiting selection. | You |
| To Do | Analyse it until implementing it is mechanical. | Agent |
| In Progress | Write the code, get it green, push it, put it up for review. | Agent |
| In Review | Read the pull request, wait for green, merge. | You |
| Done | Shipped. | You |

The order is the point. Backlog is the gate between "somebody wrote a
paragraph" and "a pull request is open". In Review is the gate before shipped.
Agents work the columns between the gates; you work the gates.

"Whose" is not a setting. A column is the agent's when a **playbook** leads
out of it for that issue's type, and yours when none does. Delete a playbook
and you have taken that column back. Add one and you have handed it over.

Columns are yours to rename, reorder, recolour and add to from the Statuses
page. Two flags matter: **terminal** marks the columns that mean shipped, and
**deferred** marks a column that means parked. A deferred column is not drawn
on the board; the issue page is the only way in, and only a person can send
something there.

## Playbooks

A playbook is one row: *from this column, to the next, for these issue types,
say this, on this model, at this effort*. The agent session that moves a card
is told exactly what the matching row says and nothing else.

"Do the next increment" is not one job. Turning a paragraph of intent into an
epic with stories under it is the hardest thinking in the flow and wants the
largest model at the highest effort. Picking up an already-specified task and
writing the code is ordinary work a smaller model does well. Playbooks are how
you spend differently on different kinds of work.

The rules:

- **Specificity wins.** A row naming the issue's type beats a row naming every
  type, so "Breakdown to Backlog, epics" can say something different from
  "Breakdown to Backlog, anything else".
- **Seven rows ship with the board**, covering every transition an agent owns,
  so a fresh install works with no configuration beyond an origin. Nothing
  re-seeds them afterwards. What you edit stays edited, and what you delete
  stays deleted.
- **Models are aliases**: `haiku`, `sonnet`, `opus`, `fable`. A playbook says
  "the big one" and should still mean it a year from now. A full model id is
  accepted for an operator with a reason to pin.
- **One issue can say the matrix is wrong about it.** An issue carries an
  optional model and effort override that beats every playbook for that issue's
  moves. It reaches that issue and nothing beneath it: an epic set to `opus`
  does not spend `opus` on its stories. There is no per-issue prompt; a
  per-issue method is a paragraph, and the description is where that goes.
- **Only a person edits a playbook.** The API refuses the write from an agent's
  key, and refuses it in the route rather than asking politely in a prompt. An
  agent that could edit its own instructions could raise its own budget, and
  that failure is unbounded spend rather than a wrong answer. If a playbook is
  wrong, an agent says so on the ticket and stops.

The first week with a new board is retuning playbooks from the Playbooks page
after watching a run go badly. That is the only way anybody ever finds the
right settings, and the page exists so it costs one edit rather than a deploy.

## What the agent picks up

Every pass the runner asks the board for the next actionable issue. The server
decides, and the answer is the same whether it is asked by the loop, by the
board, or by `hatch queue` at a terminal. An issue is actionable when all
seven of these hold:

1. **There is a column to its right, and that column is not terminal.** The
   step into Done is yours.
2. **No other runner holds a live claim on it.** Two runners are never sent at
   one ticket.
3. **Its ready date has arrived.** A card filed for next August stays folded
   off the board until then.
4. **Nobody's name is on it.** Assigning a ticket to yourself is how you take
   it off the night shift. A ticket assigned to an agent's key, or to nobody,
   is picked up as usual.
5. **It holds no unanswered question.** It is waiting on a person, and another
   agent sent at it would ask the same thing again or guess.
6. **Nothing it depends on is unfinished**, when the move is into the column
   where the code gets written. Everything to the left of that keeps moving.
7. **A playbook covers that transition for that type.** Without one there is
   nothing to tell the session, and that is exactly how a column becomes yours.

`hatch queue` prints every card a pass would look at, in the order it looks,
each with the reason it would be skipped or the transition it is clear for. It
is the answer to "why did it not pick up the ticket I meant".

## The order work is taken in

**The board is worked right to left.** The runner takes the top of the
rightmost column that still has something an agent may advance. A board worked
left to right starts everything and finishes nothing; one worked right to left
pushes whatever is furthest along over the line before opening anything new.
That is what a person does when they mean to ship, and it is what an unattended
loop does here.

Within a column, order is the order you see. Drag a card up and the runner
reaches it sooner. There is no priority field.

**Expedite** is the one flag that means *this one first*. Press it on the
issue page and the card floats to the top of its column and the dispatcher
considers every expedited card, right to left, before anything else. So an
expedited bug in Breakdown is reached before an unexpedited story in To Do.
Two things it is not:

- It is a sort key, not a gate. An expedited issue that is blocked is still
  blocked.
- It marks one issue, not the subtree under it. To point a night at one epic,
  scope the runner with `hatch go-to-work --under AER-1` instead.

Only a person can expedite. An agent that could put its own ticket ahead of
everything you filed, every night, would look fine on the board and be wrong.

**Ready and due dates.** `readyAt` is a gate: file the certificate renewal in
September and it surfaces next August on its own, rather than as a sentence in
a description nobody will see in August. `dueAt` is a deadline, drawn on the
card and warming from three days out. A past due date is accepted without
comment.

**Dependencies** are what serialise work. Filing five stories in order is not
saying they are ordered; the loop takes siblings in whatever order the board
puts them in. Five stories that are a sequence are a chain of four edges:

```
hatch depends AER-13 AER-12     # AER-13 waits on AER-12
```

An edge gates one move, into the implementation column, and clears only when
the issue it names is in a terminal column. Merged, not merely up for review.
So a chain advances at your merge, not at an agent's move. A blocked story is
still broken down, still lands in Backlog, and is still analysed; it is just not
written yet. Agents may add edges, because a planning session that has just
filed five stories is exactly who should chain them.

## Where a person is the one who moves

The design has a small number of places where the agent stops and you are the
next actor. They are worth knowing by name, because each one is a queue you
will check.

**Questions.** When a ticket needs a decision that is not an implementer's to
make, a product call, a name that will be lived with for years, a tradeoff with
no technically correct side, the agent asks on the ticket, names the choices,
and stops:

```
hatch ask AER-12 "How should drain retries be scoped?" \
    --recommend "Per-node: one budget each, so a slow node cannot starve the rest" \
    --option   "Global: one budget for the drain, simpler to reason about"
```

An open question blocks the ticket from being dispatched at all. You answer on
the issue page, where each option is something to press, or at a terminal
with `hatch answer`, which walks the open questions one at a time on purpose.
The answer rides into the next session's prompt under **Decisions already
made**, and those are settled.

**Stalls.** A session that ends with the ticket in the column it found it in is
the one dangerous failure of an unattended loop, because the next pass would
pick the same ticket, spend the same money and fail the same way all night. So
a stall writes a comment, with the command to resume that session, and opens a
question against the issue. Nothing further is dispatched there until you
answer. One bad ticket costs one increment instead of a night.

**Review.** Implementation ends in In Review with a pull request recorded on
the ticket and a comment saying what landed and what did not. The dispatcher
refuses to move anything into a terminal column. Only you decide that something
shipped, and only you decide that something is not worth doing now.

**The attention control** in the nav strip counts what is waiting on you:
pull requests nobody has reviewed, tickets in review with no pull request
recorded, and questions nobody has answered. It always draws something, so an
empty panel says "nothing is up for review" rather than nothing.

**Claims.** While a runner holds a ticket, the card shows a dot, green while
the runner is heard from and amber once it has gone quiet, and the issue page
says which checkout holds it and the last line it printed. A claim expires on
its own when a runner dies. Clearing one by hand makes the ticket claimable
again; it does not stop the runner.

## What an agent does with a ticket

The contract in the other direction. Every agent session is told to:

- **Read it.** The description is the brief; the comments, the answered
  questions and the event trail are the context.
- **Move it to In Progress before starting**, so the board says what is being
  worked on right now.
- **Comment the commit sha and the branch, and record the pull request** as a
  field on the issue, so the ticket is where somebody looks in six months.
- **Plan on the ticket, not in a chat log.** A planning session writes
  acceptance criteria into the description and files the stories or tasks the
  work breaks into.
- **File the wait, don't write it down.** Something that cannot start until a
  date gets a `readyAt`.
- **Ask and stop**, rather than guess, when a decision is not its to make.
- **Never move a ticket to a terminal or a deferred column.**
- **End with a work-log block**, a title and a summary under a hundred words,
  which the runner posts to the ticket with what the increment cost.

Every one of those calls writes an event with the key's name as the actor, so
the trail says who did what without anybody being asked to record it.

## What a night costs

The **Leaderboard** page is the meter: every unattended increment writes a row
with the session id, the issue, the duration, the token counts, the notional
cost, whether it errored, and what the session said it did. Tokens are the
headline and dollars sit beside them. It ranks the most expensive runs first,
because a leaderboard is how it gets read, and it is exactly right because it
is also how you find the playbook that is spending too much.

The **battery** in the nav strip is optional and separate: paste a token from
`claude setup-token` into Settings and Hatch shows how much headroom the
account has left. Without one there is simply no battery.

## A day with the board

1. **Morning.** Open the board. Review the pull requests in In Review and merge
   the ones that are right; the merge is what clears the dependencies waiting
   on them. Answer the questions. Move merged work to Done.
2. **Write.** Put new ideas in Draft. When one is ready to be specified, move it
   to Breakdown and an agent turns it into an epic with stories under it. Read
   what came back; move what you want built to Backlog, and drag the rest of
   the column into the order you want it taken in.
3. **Point the night.** Expedite the one thing that has to go first. Chain the
   stories that must land in sequence. Assign to yourself the one you want to
   do by hand.
4. **Leave it running.** `hatch go-to-work` with whatever bounds you want,
   a spend cap, an hour to stop at. The Runners page can pause it, stop it
   after the increment in flight, or change its scope while it runs.
5. **Next morning**, the board says what moved, what stalled, and what it cost.

## Further reading

The design document that every sentence above comes from is in the repository
as [docs/hatch.md]({{ site.github.repository_url }}/blob/HEAD/docs/hatch.md),
alongside [docs/hatch-planning.md]({{ site.github.repository_url }}/blob/HEAD/docs/hatch-planning.md),
the shapes a planning session uses, and
[docs/ethos.md]({{ site.github.repository_url }}/blob/HEAD/docs/ethos.md), the
one rule every change is held to: nothing in the repository may be true of
exactly one installation. Every host name, credential and person is a
parameter the operator supplies, which is why a stranger can run Hatch on
their own machine without asking anybody a question.
