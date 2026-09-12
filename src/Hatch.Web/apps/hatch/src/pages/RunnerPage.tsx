import { Link } from 'react-router-dom';
import { Card, PageHeader } from '@hatch/ui';
import { getRunner } from '../api/client';
import { Command } from '../components/Command';
import { detectPlatform } from '../lib/runnerPlatform';
import { useLoaded } from '../lib/useLoaded';
import type { RunnerDownloads, RunnerDownload } from '../types';

/**
 * The command reference under "4. Using it".
 *
 * The same surface `hatch --help` prints, in the same order, because a page
 * that taught a different vocabulary from the binary it hands out would be a
 * second thing to keep true. Held as data rather than as markup so the rows
 * line up on one grid however long a note runs, and every one of them is a
 * <Command/> - a reference nobody can copy out of is a reference somebody
 * retypes, and a mistyped flag is an overnight run that did not happen.
 *
 * Deliberately not the whole of it: `api`, and the planning shapes it reaches,
 * are a document rather than a row. The Docs link at the end is where they are.
 */
const REFERENCE: { title: string; blurb?: string; rows: { command: string; note: string }[] }[] = [
  {
    title: 'One increment — hatch work',
    blurb:
      'Claims a ticket, spawns one headless claude session with the prompt, model and effort its column and type call for, streams what that session does, and exits when it ends. Run it inside the checkout the board is about.',
    rows: [
      { command: 'hatch work', note: 'the next actionable issue anywhere on the board' },
      { command: 'hatch work AER-12', note: '…or this one, whatever else is above it' },
      { command: 'hatch work --under AER-1', note: '…or the next one under that epic' },
      { command: 'hatch work --dry-run', note: 'print the prompt and exit — claims nothing, spawns nothing' },
      { command: 'hatch work -i AER-12', note: 'a session you sit in, rather than a headless one' },
      { command: 'hatch work --quiet', note: 'say nothing until the increment is finished' },
      { command: 'hatch work --model opus --effort xhigh AER-12', note: 'beat the playbook, for this run only' },
    ],
  },
  {
    title: 'The loop — hatch go-to-work',
    blurb:
      'The same thing in a circle: next actionable issue, one increment, ask again — until nothing on the board is an agent’s to move, and then it waits and asks again every interval. A process per increment, so each ticket starts cold. One loop per checkout.',
    rows: [
      { command: 'hatch go-to-work', note: 'until told to stop' },
      { command: 'hatch go-to-work --once', note: 'one pass, and out' },
      { command: 'hatch go-to-work --under AER-1', note: 'only inside that epic’s subtree' },
      { command: 'hatch go-to-work --interval 300', note: 'seconds to wait when there was nothing to do (default 60)' },
      { command: 'hatch go-to-work --quiet', note: 'no per-increment stream, only what each one ended as' },
    ],
  },
  {
    title: 'Bounds, timeouts and stopping',
    blurb:
      'None of these are set by default. They are read between increments and through every wait, so the increment in flight always finishes, commits and pushes. The same three bounds are controls on the Runners page, and a value set there is folded in on the next heartbeat.',
    rows: [
      { command: 'hatch go-to-work --max-runs 5', note: 'stop after five increments' },
      { command: 'hatch go-to-work --max-spend 20', note: 'stop once the run has cost $20' },
      { command: 'hatch go-to-work --until 08:00', note: 'stop at that hour — tomorrow, if it has gone by today' },
      { command: 'hatch go-to-work --stop-file /tmp/stop', note: 'stop once that path exists' },
      { command: 'touch /tmp/stop', note: '…which is how you stop it from another terminal: no pid to find' },
      { command: 'hatch go-to-work --no-restart', note: 'never come back as a newer build (needs a supervisor either way)' },
    ],
  },
  {
    title: 'Reading the board, spending nothing',
    blurb:
      'None of these spawn anything or write anything, and none of them need a checkout. queue is the dry run for the loop, and the answer to “why did it not pick up the ticket I meant”.',
    rows: [
      { command: 'hatch board', note: 'the columns, and how many cards in each' },
      { command: 'hatch queue', note: 'every card a pass would look at, in the order it looks, and why it folds past' },
      { command: 'hatch queue AER-1', note: '…under one epic' },
      { command: 'hatch next', note: 'top workable card of “todo”' },
      { command: 'hatch next "in progress"', note: '…or of any column' },
      { command: 'hatch show AER-12', note: 'the brief, plus its comments' },
      { command: 'hatch questions', note: 'everything waiting on an answer' },
    ],
  },
  {
    title: 'Driving a ticket by hand',
    rows: [
      { command: 'hatch start AER-12', note: 'move it to “in progress”' },
      { command: 'hatch move AER-12 todo', note: '…or to any non-terminal column' },
      { command: 'hatch comment AER-12 "sha abc123 on branch aer-12-thing"', note: 'the trail somebody reads in six months' },
      { command: 'hatch pr AER-12 https://…', note: 'record the pull request — a field, not a sentence in a comment' },
      { command: 'hatch depends AER-13 AER-12', note: 'AER-13 waits on AER-12, and the loop honours it' },
      { command: 'hatch answer', note: 'answer the open questions, one at a time, here' },
    ],
  },
];

function Reference() {
  return (
    <div className="hatch-runner-reference">
      {REFERENCE.map((group) => (
        <section key={group.title} className="hatch-runner-group">
          <h3 className="hatch-runner-group-title">{group.title}</h3>
          {group.blurb && <p className="text-muted">{group.blurb}</p>}
          <dl className="hatch-runner-rows">
            {group.rows.map((row) => (
              <div key={row.command} className="hatch-runner-row">
                <dt>
                  <Command command={row.command} />
                </dt>
                <dd className="text-muted">{row.note}</dd>
              </div>
            ))}
          </dl>
        </section>
      ))}
    </div>
  );
}

/**
 * Where the runner comes from: this Hatch.
 *
 * A friend who has the stack up has one artifact and no .NET SDK, so the
 * binaries are published from the same build as the API serving this page and
 * handed out from beside it (Dockerfile.api, RunnerController). That is what
 * makes the revision honest - it is the commit the image was built from, and
 * therefore the commit the download was built from too.
 *
 * Its own page rather than a section of Settings: what it holds - a platform, a
 * download, a revision, three prerequisites, the two commands that point it at
 * this board, the reference that makes the first night confident, and what the
 * container runner beside them can and cannot do - is a page's worth, and
 * Settings has no shape for any of it.
 *
 * The reference is here and not only in the "Hatch at home" document because
 * this is the page somebody is already standing on when the binary lands in
 * their downloads folder. Sending them elsewhere to find out what to type is
 * the gap between having the runner and using it.
 */
export function RunnerPage() {
  const { data: runner, error } = useLoaded<RunnerDownloads>(getRunner);

  // Read once at render from the browser itself. There is no reliable way to
  // tell an Apple Silicon Mac from an Intel one here, so a Mac is guessed as
  // arm64 and the other three sit underneath - see lib/runnerPlatform.ts.
  const detected = detectPlatform(typeof navigator === 'undefined' ? '' : navigator.userAgent);

  const downloads = runner?.downloads ?? [];
  const mine = downloads.find((d) => d.rid === detected) ?? null;
  const others = downloads.filter((d) => d !== mine);

  // What this Hatch is reached at, taken from the page rather than typed by
  // hand: the browser is already standing at the right origin, and the address
  // bar is the one thing about the install nobody can get wrong.
  const origin = window.location.origin;

  return (
    <div className="hatch-page">
      <PageHeader
        title="Runner"
        description="The loop runs on your machine and talks to this Hatch. Download it here - it is built from the same image that is serving this page, so the two are never a version apart."
      />

      {error && <p className="text-danger">{error}</p>}

      <Card>
        <h2 className="hatch-section-title">1. Download</h2>

        {/* Only what this build actually published is offered. An image built
            before the publish step - or a `dotnet run` from a checkout - has
            none, and saying so is better than four links that 404. */}
        {runner && downloads.length === 0 && (
          <p className="text-muted">
            This build published no runner binaries. Build them from a checkout with{' '}
            <code>make publish-hatch</code>, or deploy an image built since the runner was added.
          </p>
        )}

        {mine && (
          <p className="hatch-runner-pick">
            <a className="hatch-runner-download" href={mine.url} download={mine.fileName}>
              Download for {mine.platform}
            </a>
            <span className="text-muted"> — saves as {mine.fileName}</span>
          </p>
        )}

        {others.length > 0 && (
          <p className="hatch-runner-others text-muted">
            {mine ? 'On a different machine: ' : 'Choose your platform: '}
            {others.map((other: RunnerDownload, at) => (
              <span key={other.rid}>
                {at > 0 && ' · '}
                <a href={other.url} download={other.fileName}>
                  {other.platform}
                </a>
              </span>
            ))}
          </p>
        )}

        {runner && (
          <p className="hatch-runner-revision text-muted">
            {/* The image's own revision, on the same answer as the list above
                rather than from a second call - a page that asked twice could
                draw a download from one build beside a revision from another. */}
            Built from <code>{runner.revision}</code>
          </p>
        )}

        <p className="text-muted">
          Put it on your PATH as <code>hatch</code> (<code>hatch.exe</code> on Windows). macOS and
          Linux need it marked executable: <code>chmod +x hatch</code>.
        </p>
      </Card>

      <Card>
        <h2 className="hatch-section-title">2. What to have first</h2>
        <ul className="hatch-runner-needs">
          <li>
            <a href="https://www.docker.com/products/docker-desktop/" target="_blank" rel="noreferrer">
              Docker Desktop
            </a>{' '}
            running — the loop builds and runs against containers.
          </li>
          <li>
            <a href="https://git-scm.com/downloads" target="_blank" rel="noreferrer">
              git
            </a>{' '}
            installed — <code>go-to-work</code> branches, commits and pushes from a checkout.
          </li>
          <li>
            The{' '}
            <a href="https://docs.claude.com/en/docs/claude-code/setup" target="_blank" rel="noreferrer">
              claude CLI
            </a>{' '}
            installed and logged in — it is what each increment actually runs as.
          </li>
        </ul>
      </Card>

      <Card>
        <h2 className="hatch-section-title">3. Two commands</h2>
        <p className="text-muted">
          The first points the runner at this Hatch and writes it where it follows you between
          checkouts. The second takes the next actionable issue and starts working.
        </p>
        <div className="hatch-runner-commands">
          <Command command={`hatch config --origin ${origin}`} />
          <Command command="hatch go-to-work" />
        </div>
        <p className="text-muted">
          Run <code>go-to-work</code> from inside a checkout of the repository the board is about.
          If this Hatch has its wall up you will need a key too — <code>hatch config</code> asks for
          one.
        </p>
      </Card>

      <Card>
        <h2 className="hatch-section-title">4. Using it</h2>
        <p className="text-muted">
          The whole surface is <code>hatch --help</code>, and every command takes <code>-h</code> for
          its own. These are the ones a working day is made of. Only <code>work</code> and{' '}
          <code>go-to-work</code> need a git checkout — the rest are one request and a sentence about
          the answer, so <code>hatch board</code> from anywhere is the ordinary case.
        </p>
        <Reference />
        <p className="text-muted">
          Before every increment the loop fetches, puts the checkout back on the default branch, and
          stashes anything uncommitted — named for the hour it was taken, recoverable with{' '}
          <code>git stash pop</code>. Committed work is never at risk. The practical consequence:
          don't leave something half-finished in the tree and then start the loop in it.
        </p>
        <p className="text-muted">
          What each session is told — its prompt, its model, its effort — comes from a{' '}
          <Link to="/playbooks">playbook</Link>, a row per column transition and issue type. Those are
          yours to edit and not your agents': the API refuses the write from a key.
        </p>
        <p className="text-muted">
          {/* A static file shipped beside this bundle, so a plain anchor - see
              the Docs link in the nav strip, which goes to the same page. */}
          The long version, including the block to paste into your repository's <code>CLAUDE.md</code>{' '}
          so your agents know how to reach this board:{' '}
          <a href="/apps/hatch/hatch-at-home.md">Hatch at home</a>.
        </p>
      </Card>

      {/* The container runner, described rather than offered: there is no
          button here, because starting it is a line in the terminal that
          brought the stack up and this page cannot reach that terminal. What
          it can do is say honestly which repositories it suits, so that the
          decision is made before the pull rather than after the first failed
          build. AERIE-939, criterion 7 - the same paragraph is in the "Hatch
          at home" document, for the person who never opens this page. */}
      <Card>
        <h2 className="hatch-section-title">Or run it as a container</h2>
        <p className="text-muted">
          The same stack can start a container that carries the runner, <code>git</code> and the{' '}
          <code>claude</code> CLI, mounts one of your checkouts and works tickets under this board's
          control — <code>docker compose --profile runner up -d</code>, with{' '}
          <code>HATCH_CHECKOUT</code> naming the repository. It takes its Claude credential from the
          Settings page, so there is nothing to log in to.
        </p>
        <p className="text-muted">
          It carries no language runtimes, though — no .NET, no Node, no Python, no compilers. So it
          can plan, break work down, analyse and write code in any repository at all, and commit and
          push what it wrote; it <em>cannot</em> build or test one whose toolchain it lacks, and no
          general image has everybody's. Where "done" means a green build, the download above is the
          one to use: it runs on your machine, with whatever you already have installed. The "Hatch
          at home" document has the full recipe, including how it gets something to push with.
        </p>
      </Card>
    </div>
  );
}
