import { Card, PageHeader } from '@aerie/ui';
import { getRunner } from '../api/client';
import { Command } from '../components/Command';
import { detectPlatform } from '../lib/runnerPlatform';
import { useLoaded } from '../lib/useLoaded';
import type { RunnerDownloads, RunnerDownload } from '../types';

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
 * download, a revision, three prerequisites, two commands, and what the
 * container runner beside them can and cannot do - is a page's worth, and
 * Settings has no shape for any of it.
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
