---
description: Take a ticket off the board, branch, implement it, push, and report back on the ticket
argument-hint: "[AER-12]  (omit for the top of todo)"
allowed-tools: Bash(./scripts/hatch.sh:*), Bash(git:*), Bash(make:*), Read, Edit, Write, Grep, Glob
---

Work one Hatch ticket end to end. `scripts/hatch.sh` is the client; read its
header if a call is not obvious.

(From a terminal, `./scripts/hatch.sh work` does this and chooses its own model
and effort from the ticket's playbook. This command is the hands-on version:
same flow, this session, no spawn.)

## Which ticket

$ARGUMENTS

If an issue key was given above, that is the ticket. If nothing was given, take
the top of the todo column:

```
./scripts/hatch.sh next
```

If that says nothing is workable, stop and say so — do not go hunting in other
columns.

## Then, in order

1. **Read the brief.** `./scripts/hatch.sh show <KEY>`. The description is the
   brief and its acceptance criteria are the definition of done. If it has
   children, read them too (`./scripts/hatch.sh api GET
   /api/hatch/issues?parentKey=<KEY>`) — a story's tasks are the actual work.

2. **Say what you are doing, before doing it.** Move the ticket so the board is
   honest about what is being worked on right now:

   ```
   ./scripts/hatch.sh start <KEY>
   ```

3. **Branch from the remote, not from local main.** Another session may share
   this tree, and a branch cut from a local main carries their unpushed commits
   into your push:

   ```
   git fetch origin
   git switch -c <key-lowercased>-<short-slug> origin/main
   ```

4. **Implement it.** House rules apply and are not negotiable for this run:
   build with `make` (`make build`, `make test-api`, `make test-web`) and never
   a bare `dotnet`; no hardcoded domains, addresses, hostnames or people
   anywhere, including in comments; read the pattern file a plan names before
   writing the thing it patterns.

5. **Green before pushed.** Lint, build and tests are the implementer's
   definition of done — the operator does all browser and UI verification, so
   do not claim a screen works, only that it builds. If something is red and
   you cannot fix it, stop at step 6 and say so rather than pushing red.

6. **Commit and push.** This command is the standing ask for both — the usual
   "never commit unless asked" is satisfied by the invocation, and it covers
   this ticket's work only:

   ```
   git push -u origin <branch>
   ```

   Commit subject in house style: `Area: what changed, as a sentence`.

7. **Report back on the ticket, not in the chat.** The ticket is where somebody
   looks in six months:

   ```
   ./scripts/hatch.sh comment <KEY> "<branch>, <sha> — what landed, and what did not"
   ```

8. **Leave it in progress.** Never move a ticket to a terminal column; only the
   operator decides that something shipped. `hatch.sh` will refuse anyway.

Finish by telling me the key, the branch, the sha, and anything you left
undone.
