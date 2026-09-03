import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, Card, EmptyState, Field, PageHeader, Text } from '@aerie/ui';
import type { BadgeTone } from '@aerie/ui';
import { getProjects, previewImport, previewText, runImport } from '../api/client';
import { message } from '../lib/errors';
import type { ImportResult, ParsedEpic, PlanState, Project } from '../types';

/* What each state is called on screen. The parser's three states are matched to
   real columns by the server - it is the only thing that knows what this board
   calls them - so the preview names the state rather than promising a column. */
const STATE_LABELS: Record<PlanState, string> = {
  Todo: 'todo',
  InProgress: 'in progress',
  Done: 'done',
};

const STATE_TONES: Record<PlanState, BadgeTone> = {
  Todo: 'muted',
  InProgress: 'primary',
  Done: 'success',
};

/**
 * Plans in, one directory at a time: pick the `.md` files, read what they would
 * become, then write it.
 *
 * The preview is not a nicety. An import is one button and forty issues, and
 * there is no undo for it - so the shape of the thing is shown first, and the
 * import button is only reachable from the other side of it.
 *
 * This page only ever copies. Retiring the source `.md` file stays a deliberate
 * manual act, per the plans lifecycle in docs/plans/README.md.
 */
export function ImportPage() {
  const [projects, setProjects] = useState<Project[]>([]);
  const [projectId, setProjectId] = useState<number | null>(null);
  const [docs, setDocs] = useState<ParsedEpic[] | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // The pasted plan, which is one document at a time by construction - the
  // shape of the form is the shape of the thing.
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');

  useEffect(() => {
    getProjects().then(setProjects).catch((err: unknown) => setError(message(err)));
  }, []);

  // The first project, until somebody picks another - the same choice the New
  // issue dialog makes, and for the same reason.
  const chosen = projectId ?? projects[0]?.id ?? null;

  // Both halves, because the server refuses either one missing and a button
  // that reports that after a round trip is a button that wasted a click.
  const pastable = title.trim() !== '' && body.trim() !== '';

  async function preview(files: FileList | null) {
    if (!files || files.length === 0) return;

    setBusy(true);
    setResult(null);
    try {
      setDocs(await previewImport([...files]));
      setError(null);
    } catch (err) {
      setDocs(null);
      setError(message(err));
    } finally {
      setBusy(false);
    }
  }

  /**
   * The same look, for text that never was a file. Wrapped in an array on the
   * way out so everything downstream - the preview cards, the count on the
   * button, the import itself - is the upload path's, unchanged.
   */
  async function previewPasted() {
    if (!pastable) return;

    setBusy(true);
    setResult(null);
    try {
      setDocs([await previewText({ title: title.trim(), body })]);
      setError(null);
    } catch (err) {
      setDocs(null);
      setError(message(err));
    } finally {
      setBusy(false);
    }
  }

  async function importDocs() {
    if (!docs || chosen === null) return;

    setBusy(true);
    try {
      setResult(await runImport({ projectId: chosen, docs }));
      // The tree is gone once it is written: leaving it on screen beside a
      // result invites a second click, and a second click is a second copy of
      // every issue. The pasted text goes with it, for the same reason - it is
      // the only one of the two inputs that would otherwise still be sitting
      // there, filled in and one Preview away from a duplicate import.
      setDocs(null);
      setTitle('');
      setBody('');
      setError(null);
    } catch (err) {
      setError(message(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="hatch-page">
      <PageHeader
        title="Import"
        description="A markdown plan becomes an epic, each ## Phase a story, each checkbox a task."
      />

      {error && <p className="text-danger">{error}</p>}

      <Card>
        <div className="hatch-form">
          <Field label="Project" hint="Where the imported issues get their keys.">
            <select
              value={chosen ?? ''}
              disabled={projects.length === 0}
              onChange={(e) => setProjectId(Number(e.target.value))}
            >
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.key} — {p.name}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Plan files" hint="One or more .md files. Nothing is written until you say so.">
            <input
              type="file"
              accept=".md,text/markdown"
              multiple
              onChange={(e) => {
                // Cleared so choosing the same file twice still fires a change
                // - a re-read after an edit is the ordinary second use of this.
                //
                // After the upload, never beside it. `preview` starts the fetch
                // synchronously - the FormData is built and the request issued
                // before its first await - so clearing here without waiting
                // pulls the input's FileList out from under a body that is
                // still being streamed. The part arrives short, the closing
                // multipart boundary never lands where the parser expects it,
                // and the server reports the read as an unexpected end of
                // stream: a 400 naming the form, from a page that looks like it
                // sent one.
                const input = e.target;
                void preview(input.files).finally(() => {
                  input.value = '';
                });
              }}
            />
          </Field>
        </div>
      </Card>

      {/* The other way in. A plan does not have to be a file to be worth
          filing: a page of notes from a chat window, or a phase list somebody
          typed out, would otherwise have to be saved to docs/plans first and
          uploaded back - a round trip through the filesystem that buys nothing
          and leaves a scratch file behind to retire. */}
      <Card>
        <h2 className="hatch-section-title">Or paste one</h2>

        <div className="hatch-form">
          <Field
            label="Title"
            hint="What this document is called. Every issue from it carries the name, and it titles the epic unless the body opens with its own # heading."
          >
            <input
              type="text"
              value={title}
              maxLength={300}
              onChange={(e) => setTitle(e.target.value)}
            />
          </Field>

          <Field
            label="Body"
            hint="Markdown, the same shape a plan file has: ## Phase opens a story, each checkbox under it is a task."
          >
            <textarea rows={16} value={body} onChange={(e) => setBody(e.target.value)} />
          </Field>

          <div className="hatch-form-actions">
            <Button loading={busy} disabled={!pastable} onClick={() => void previewPasted()}>
              Preview
            </Button>
          </div>
        </div>
      </Card>

      {docs && docs.length > 0 && (
        <>
          {docs.map((doc) => (
            <DocPreview key={doc.filename} doc={doc} />
          ))}

          <div className="hatch-form-actions">
            <Button
              variant="primary"
              loading={busy}
              disabled={chosen === null}
              onClick={() => void importDocs()}
            >
              Import {count(docs)} into {projects.find((p) => p.id === chosen)?.key ?? 'nothing'}
            </Button>
          </div>
        </>
      )}

      {docs?.length === 0 && <EmptyState message="Those files held no plans." />}

      {result && (
        <Card>
          <h2 className="hatch-section-title">Imported {result.issueCount} issues</h2>
          <ul className="hatch-child-list">
            {result.epics.map((epic) => (
              <li key={epic.key}>
                <Link to={`/issues/${epic.key}`}>
                  <code>{epic.key}</code> {epic.title}
                </Link>{' '}
                <Text tone="muted">
                  — {epic.filename}, {plural(epic.storyCount, 'story', 'stories')}, {plural(epic.taskCount, 'task')}
                </Text>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </div>
  );
}

/** One document, as the tree it would become - every phase, and every task
    under it. The counts say how much; the titles are what says whether it was
    read correctly, and they are the whole reason this page exists: an import is
    one button and forty issues, so the chop has to be checkable before it is
    committed rather than after. */
function DocPreview({ doc }: { doc: ParsedEpic }) {
  const tasks = doc.stories.reduce((total, story) => total + story.tasks.length, 0);

  return (
    <Card>
      <div className="hatch-section-head">
        <h2 className="hatch-section-title">
          {doc.title} <Text tone="muted">— {doc.filename}</Text>
        </h2>
        <StateBadge state={doc.state} />
      </div>

      <Text tone="muted">
        1 epic, {plural(doc.stories.length, 'story', 'stories')}, {plural(tasks, 'task')}
      </Text>

      <ul className="hatch-child-list">
        {doc.stories.map((story) => (
          <li key={story.title}>
            <div className="hatch-import-row">
              <span>{story.title}</span>
              <Text tone="muted">{plural(story.tasks.length, 'task')}</Text>
              <StateBadge state={story.state} />
            </div>

            {story.tasks.length > 0 && (
              <ul className="hatch-import-tasks">
                {story.tasks.map((task, i) => (
                  // The index, because two checkboxes in one phase are allowed
                  // to read the same and this list is never reordered.
                  <li key={i} className="hatch-import-row">
                    <span>{task.title}</span>
                    <StateBadge state={task.state} />
                  </li>
                ))}
              </ul>
            )}
          </li>
        ))}
      </ul>
    </Card>
  );
}

function StateBadge({ state }: { state: PlanState }) {
  return <Badge tone={STATE_TONES[state]}>{STATE_LABELS[state]}</Badge>;
}

/** How many issues an import is about to file - the number on the button, so
    "import" is never a click into an unknown quantity. */
function count(docs: ParsedEpic[]): string {
  const issues = docs.reduce(
    (total, doc) => total + 1 + doc.stories.length + doc.stories.reduce((n, s) => n + s.tasks.length, 0),
    0,
  );

  return plural(issues, 'issue');
}

function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}
