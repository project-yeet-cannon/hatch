---
title: Quick start
permalink: /
---

<div class="hero" markdown="1">

# Hatch

A kanban board worked by agents as much as by people. You write the tickets,
answer the questions and merge the pull requests. An agent takes the rest,
one ticket at a time, all night if you let it.

This page takes you from nothing to a board running on your machine and an
agent working it from your terminal. Ten minutes if the downloads are quick.

</div>

<ul class="cards">
  <li><a href="{{ '/how-to-hatch/' | relative_url }}">How to Hatch</a><span>Playbooks, the order work is taken in, and where a person is the one who moves things.</span></li>
  <li><a href="{{ '/running-the-agent/' | relative_url }}">Running the agent</a><span>The runner on your machine: one increment, the loop, and what it does to a checkout.</span></li>
  <li><a href="{{ '/container-runner/' | relative_url }}">The container runner</a><span>The same runner beside the stack in Docker, and what it cannot do.</span></li>
  <li><a href="{{ '/agent-manual/' | relative_url }}">Agent manual</a><span>Every command, flag, variable, exit code and behaviour of the runner.</span></li>
</ul>

## 1. Install these first

The board runs in Docker. The agent runs bare on your machine, so it needs
git and the Claude Code CLI installed where it can find them.

| What | Why | Get it |
|---|---|---|
| **Docker Desktop** | Runs the board: Postgres, a migration, the API. Builds the image from source, so there is nothing else to install for the board itself. | [docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop/) |
| **git** | The runner fetches, branches, commits and pushes from a checkout. | [git-scm.com/install](https://git-scm.com/install/) ([macOS](https://git-scm.com/install/mac) · [Windows](https://git-scm.com/install/windows) · [Linux](https://git-scm.com/install/linux)) |
| **Claude Code CLI**, logged in | Each increment is one headless `claude` session. Needs a Claude Pro, Max, Team or Enterprise plan, or a Console account. | [code.claude.com/docs/en/setup](https://code.claude.com/docs/en/setup) |
| **A repository to work on** | A git checkout with a remote the runner can push to. Hatch's own repository is fine for a first run. | Your own |
| **The `hatch` binary** | The runner. Your own board hands it out, built from the same commit as the board, so there is nothing to download until the board is up. | Step 3, below |

Install the Claude Code CLI with the native installer:

```
curl -fsSL https://claude.ai/install.sh | bash
```

On Windows, in PowerShell: `irm https://claude.ai/install.ps1 | iex`. Then run
`claude` once and follow the browser prompt to log in.

Not needed: the .NET SDK, Node, the GitHub CLI. The binary is self-contained
and the runner records a pull request by URL rather than opening one.

## 2. Bring up the board

```
git clone {{ site.github.clone_url | default: "<the repository's clone URL>" }} hatch
cd hatch
docker compose up -d
```

Then open **http://localhost:8080/apps/hatch/**. The first run takes a few
minutes: it builds the image, runs the migration to completion, and only then
starts the API. Compose says nothing about the address once it has gone to the
background, which is why it is written here.

If port 8080 is taken, `HATCH_PORT=9090 docker compose up -d` moves it. In
PowerShell that is `$env:HATCH_PORT=9090; docker compose up -d`.

## 3. Then, by hand

These are the steps a person does in the browser and the terminal once the
stack is running. They are in the order to do them in.

{:.steps}
1. **Name yourself.** There is no sign-in on a board started this way. It
   signs what you do with your shell's user name and calls you `friend` if it
   has none. **Settings → Your name** fixes that, and every comment and event
   from then on carries it.

2. **Make a project.** **Projects → New project**, with a short key. `HOME`
   gives you `HOME-1`, `HOME-2`. Until there is a project there is nothing for
   an issue key to be made of.

3. **Download the runner.** **Runner** in the nav detects your platform and
   offers the matching binary. Put it on your `PATH` as `hatch` (`hatch.exe`
   on Windows). On macOS and Linux, `chmod +x hatch` first.

4. **Point it at the board.** Once, from any directory:

   ```
   hatch config --origin http://localhost:8080
   ```

   It asks for an API key. Against this board, leave it blank. The setting is
   written to a per-user file outside every repository, so it follows you
   between checkouts.

5. **Tell your repository how to reach the board.** The agent session that
   works a ticket learns about Hatch from your repository's `CLAUDE.md`. Your
   board ships the block to paste: open **Docs** in the nav, find "The
   `CLAUDE.md` block", and paste it into the `CLAUDE.md` of the repository the
   board is about. Nothing in it needs editing.

6. **Write a ticket.** On the board, add an issue under your project in
   **Draft**. The description is the brief: say what you want, and what
   "done" looks like. When it is ready to be specified, drag it to
   **Breakdown**. That is the first column an agent owns.

7. **Check what the runner would do.** From inside your repository's checkout:

   ```
   hatch queue
   ```

   It prints every card a pass would look at and either the reason it would
   skip it or the transition it is clear for. It spawns nothing and writes
   nothing.

8. **Start the runner.** From the same checkout, with anything uncommitted
   already committed or stashed, because the loop resets the tree before
   every increment:

   ```
   hatch go-to-work --max-spend 10
   ```

   One increment on the top ticket, then the next, until the board has
   nothing an agent may move. `--max-spend` is optional; `--once` runs one
   pass and stops. Ctrl-C stops it after letting go of the ticket.

9. **Come back to the board.** The **Runners** page shows the loop and can
   pause it, stop it after the increment in flight, or cap it. The attention
   control in the nav strip counts what is waiting on you: questions to
   answer, pull requests to review. Answer, review, merge, and move merged
   work to **Done**. Only you move a ticket there.

That is the whole loop. [How to Hatch](how-to-hatch.md) is what to expect
from it, and [Running the agent](running-the-agent.md) is every knob on the
runner.

## Or: let an agent do the install

If you would rather not do step 1 and step 2 yourself, paste the block below
into a coding agent running on the machine with full system access, such as
Claude Code. It installs what is missing, brings the board up, and then hands
you the short list of things only you can do. It stops before anything that
needs your password and before anything that needs you to log in.

````
I want to run Hatch, a self-hosted kanban board worked by AI agents, on this
machine. Do the installation for me, then hand back the steps only I can do.

Rules:
- Before running anything that needs an administrator password or sudo, tell
  me what it is and wait for my go-ahead.
- Do not log in to anything on my behalf, and do not paste any credential,
  token or key into a file, a command or a message.
- Check whether each tool is already installed before installing it, and use
  this machine's own package manager where there is one (Homebrew on macOS,
  winget on Windows, apt/dnf on Linux). Install Homebrew first on macOS if it
  is missing.
- Tell me what you did, briefly, at the end.

Steps:

1. Detect the operating system and CPU architecture.

2. Make sure git is installed and `git --version` works.
   Reference: https://git-scm.com/install/

3. Make sure Docker with the compose plugin is installed and the engine is
   running. On macOS and Windows that is Docker Desktop
   (https://www.docker.com/products/docker-desktop/); on Linux, Docker Engine
   plus the docker-compose-plugin. Start Docker Desktop if it is installed but
   not running. `docker version` must print a Server section as well as a
   Client section before you continue. If a reboot or sign-out is required,
   stop and tell me.

4. Make sure the Claude Code CLI is installed. Use the native installer
   (macOS/Linux/WSL: `curl -fsSL https://claude.ai/install.sh | bash`;
   Windows PowerShell: `irm https://claude.ai/install.ps1 | iex`).
   Reference: https://code.claude.com/docs/en/setup
   `claude --version` must work afterwards. Do not log in for me.

5. Clone the Hatch repository into a directory called `hatch` under my home
   directory, unless a checkout already exists there:
   `git clone {{ site.github.clone_url | default: "<the repository's clone URL>" }} hatch`

6. From that directory run `docker compose up -d`. The first run builds an
   image and can take several minutes. Then poll
   http://localhost:8080/apps/hatch/ until it answers with HTTP 200, waiting
   up to ten minutes. If port 8080 is already in use, use a free port by
   setting HATCH_PORT for the compose command and tell me which port.

7. Stop there. Print the address of the board and this checklist for me to
   do by hand, filling in the port you used:

   - Run `claude` once in a terminal and follow the browser prompt to log in.
   - Open the board. Settings -> Your name: set it.
   - Projects -> New project: give it a short key such as HOME.
   - Runner (in the nav): download the binary for this machine, put it on
     the PATH as `hatch` (chmod +x on macOS/Linux).
   - Run `hatch config --origin http://localhost:<port>` and leave the key
     blank.
   - Open Docs in the nav, copy "The CLAUDE.md block" into the CLAUDE.md of
     the repository I want the agent to work on.
   - Write a first ticket in Draft and drag it to Breakdown.
   - From inside that repository's checkout run `hatch queue`, then
     `hatch go-to-work --once`.

Do not run `hatch go-to-work` yourself.
````

## What you have now

- **A board** at your address, its data in one Docker volume that survives
  `docker compose down`. [Operating the stack](operating-the-stack.md) covers
  upgrades, backups and what to do when something is wrong.
- **A runner** that talks to the board over HTTP and spends each increment in
  a `claude` session inside your checkout, with permission prompts bypassed,
  because nothing is there to answer them. That is a deliberate grant and it
  is why `hatch go-to-work` is a command you type.
- **A contract** between you and the agents: they read the ticket, move it,
  comment the commit, record the pull request, ask when a decision is not
  theirs, and never mark anything done. [How to Hatch](how-to-hatch.md) is that
  contract in full.
