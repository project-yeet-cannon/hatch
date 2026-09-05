import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Card, Field, PageHeader } from '@aerie/ui';
import {
  addComment,
  deleteIssue,
  getBoard,
  getComments,
  getEvents,
  getIssue,
  patchIssue,
} from '../api/client';
import { MomentChip } from '../components/MomentChip';
import { StatusPill } from '../components/StatusPill';
import { MomentField } from '../components/MomentField';
import { TypeBadge } from '../components/TypeBadge';
import { statusVars } from '../lib/color';
import { message } from '../lib/errors';
import { renderMarkdown } from '../lib/markdown';
import { openQuestions } from '../lib/questions';
import { ISSUE_TYPES, LEGAL_PARENT_TYPES } from '../types';
import type { Board, Comment, Issue, IssueEvent, IssueType, Status } from '../types';

export function IssuePage() {
  const { key = '' } = useParams();
  const navigate = useNavigate();

  const [issue, setIssue] = useState<Issue | null>(null);
  const [board, setBoard] = useState<Board | null>(null);
  const [comments, setComments] = useState<Comment[]>([]);
  const [events, setEvents] = useState<IssueEvent[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [loaded, loadedBoard, loadedComments, loadedEvents] = await Promise.all([
        getIssue(key),
        getBoard(),
        getComments(key),
        getEvents(key),
      ]);
      setIssue(loaded);
      setBoard(loadedBoard);
      setComments(loadedComments);
      setEvents(loadedEvents);
      setError(null);
    } catch (err) {
      setError(message(err));
    }
  }, [key]);

  useEffect(() => {
    void load();
    const onFocus = () => void load();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [load]);

  const save = useCallback(
    async (patch: Parameters<typeof patchIssue>[1]) => {
      try {
        await patchIssue(key, patch);
        await load();
      } catch (err) {
        setError(message(err));
      }
    },
    [key, load],
  );

  if (error && !issue) return <p className="text-danger">{error}</p>;
  if (!issue || !board) return <p className="text-muted">Loading…</p>;

  // The legal parents: same project, a type this issue may hang under, and
  // never itself. The server decides too - this only keeps the picker from
  // offering something it will refuse.
  const legal = LEGAL_PARENT_TYPES[issue.type];
  const parents = board.issues.filter(
    (i) => i.projectKey === issue.projectKey && i.key !== issue.key && legal.includes(i.type),
  );

  // Sitting in a column that means it shipped, so the due chip stops warning -
  // the same rule the board follows, for the same reason.
  const terminal = board.statuses.find((s) => s.id === issue.statusId)?.isTerminal ?? false;

  async function remove() {
    if (!confirm(`Delete ${key}? Its comments and its history go with it.`)) return;
    try {
      await deleteIssue(key);
      void navigate('/');
    } catch (err) {
      setError(message(err));
    }
  }

  return (
    <div className="hatch-issue-page">
      <PageHeader
        title={<InlineTitle issue={issue} onSave={(title) => void save({ title })} />}
        description={
          <span className="hatch-issue-meta">
            <span className="hatch-issue-key">{issue.key}</span>
            <TypeBadge type={issue.type} />
            {issue.parentKey && <Link to={`/issues/${issue.parentKey}`}>↳ {issue.parentKey}</Link>}
            <MomentChip kind="ready" value={issue.readyAt} />
            <MomentChip kind="due" value={issue.dueAt} muted={terminal} />
            <span className="text-muted">
              filed by {issue.createdBy} on {new Date(issue.createdAt).toLocaleDateString()}
            </span>
          </span>
        }
        actions={
          <Button variant="danger" onClick={() => void remove()}>
            Delete
          </Button>
        }
      />

      {error && <p className="text-danger">{error}</p>}

      {/* Above everything the page lets you change, because it is the one thing
          on it that something else is waiting for. An issue holding an
          unanswered question is not dispatched at all - see WorkController - so
          until this card is empty the ticket does not move. */}
      <Waiting issueKey={key} comments={comments} onAnswered={() => void load()} onError={setError} />

      <StatusBar
        statuses={board.statuses}
        statusId={issue.statusId}
        onMove={(statusId) => void save({ statusId })}
      />

      <Card>
        <div className="hatch-issue-controls">
          <Field label="Type">
            <select value={issue.type} onChange={(e) => void save({ type: e.target.value as IssueType })}>
              {ISSUE_TYPES.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
          </Field>

          {/* The empty option is the clear, and it maps to the empty string the
              API reads as "no parent" - see IssuePatchRequest. */}
          <Field label="Parent" hint={`A ${issue.type} hangs under ${legal.join(' or ')}.`}>
            <select value={issue.parentKey ?? ''} onChange={(e) => void save({ parentKey: e.target.value })}>
              <option value="">— none —</option>
              {parents.map((p) => (
                <option key={p.key} value={p.key}>
                  {p.key} — {p.title}
                </option>
              ))}
            </select>
          </Field>

          <MomentField
            label="Ready"
            hint="Folded off the board until this day."
            value={issue.readyAt}
            onChange={(readyAt) => void save({ readyAt })}
          />

          <MomentField
            label="Due"
            hint="A past date is fine - nothing here argues with one."
            value={issue.dueAt}
            onChange={(dueAt) => void save({ dueAt })}
          />
        </div>
      </Card>

      <Description issue={issue} onSave={(description) => void save({ description })} />

      {issue.childKeys.length > 0 && (
        <Card>
          <h2 className="hatch-section-title">Children</h2>
          <ul className="hatch-child-list">
            {issue.childKeys.map((child) => (
              <li key={child}>
                <Link to={`/issues/${child}`}>{child}</Link>
              </li>
            ))}
          </ul>
        </Card>
      )}

      <Comments issueKey={key} comments={comments} onAdded={() => void load()} onError={setError} />

      <EventTrail events={events} />
    </div>
  );
}

/**
 * Where this issue is, and every other place it could be.
 *
 * Status was a <select> in a row of five pickers, which made "what is the state
 * of this thing" - the first question anybody opens an issue with - the same
 * size as its ready date. Here it is the page's own band: the column it is in,
 * in that column's colour, and the whole board's worth of columns beside it as
 * one press each.
 *
 * Every column is offered, in board order, because Hatch has no transition
 * rules on purpose (docs/plans/pjm.md, "Non-goals") - any status to any status,
 * we trust ourselves.
 */
function StatusBar({
  statuses,
  statusId,
  onMove,
}: {
  statuses: Status[];
  statusId: number;
  onMove: (statusId: number) => void;
}) {
  const current = statuses.find((s) => s.id === statusId);

  return (
    <section className="hatch-status-bar" style={statusVars(current?.color)} aria-label="Status">
      <div className="hatch-status-bar-now">
        <span className="hatch-status-bar-label">Status</span>
        {current ? <StatusPill status={current} size="lg" /> : <span className="text-muted">unknown</span>}
      </div>

      <div className="hatch-status-steps" role="group" aria-label="Move this issue">
        {statuses.map((status) => {
          const here = status.id === statusId;
          return (
            <button
              key={status.id}
              type="button"
              className={`hatch-status-step${here ? ' here' : ''}`}
              style={statusVars(status.color)}
              aria-pressed={here}
              disabled={here}
              onClick={() => onMove(status.id)}
            >
              {status.name}
            </button>
          );
        })}
      </div>
    </section>
  );
}

/** The title, editable where it is read. A separate edit screen for one string is a screen too many. */
function InlineTitle({ issue, onSave }: { issue: Issue; onSave: (title: string) => void }) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(issue.title);
  const [known, setKnown] = useState(issue.title);

  // Re-syncs when the title changes underneath - a refetch on focus, or
  // somebody else's edit - so the editor does not hand back a stale string on
  // its next save. Adjusted during render rather than in an effect: an effect
  // would paint the old value first, and React documents this shape for
  // exactly this case.
  if (issue.title !== known) {
    setKnown(issue.title);
    setDraft(issue.title);
  }

  if (!editing) {
    return (
      <button type="button" className="hatch-inline-title" onClick={() => setEditing(true)}>
        {issue.title}
      </button>
    );
  }

  function commit() {
    setEditing(false);
    if (draft.trim() && draft !== issue.title) onSave(draft.trim());
  }

  return (
    <input
      className="hatch-inline-title-input"
      value={draft}
      autoFocus
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') commit();
        if (e.key === 'Escape') {
          setDraft(issue.title);
          setEditing(false);
        }
      }}
    />
  );
}

/**
 * The description: raw markdown in a textarea, with a preview toggle. Stored
 * and edited raw on purpose - what the database holds is what somebody wrote.
 */
function Description({ issue, onSave }: { issue: Issue; onSave: (description: string) => void }) {
  const [draft, setDraft] = useState(issue.description);
  const [known, setKnown] = useState(issue.description);
  const [preview, setPreview] = useState(true);

  // Same reasoning as the title's - see InlineTitle.
  if (issue.description !== known) {
    setKnown(issue.description);
    setDraft(issue.description);
  }

  return (
    <Card>
      <div className="hatch-section-head">
        <h2 className="hatch-section-title">Description</h2>
        <div className="hatch-section-actions">
          <Button onClick={() => setPreview(!preview)}>{preview ? 'Edit' : 'Preview'}</Button>
          {!preview && (
            <Button variant="primary" disabled={draft === issue.description} onClick={() => onSave(draft)}>
              Save
            </Button>
          )}
        </div>
      </div>

      {preview ? (
        issue.description.trim() ? (
          // Sanitized by renderMarkdown - nothing from the database is trusted markup.
          <div className="hatch-markdown" dangerouslySetInnerHTML={{ __html: renderMarkdown(issue.description) }} />
        ) : (
          <p className="text-muted">No description yet.</p>
        )
      ) : (
        <textarea className="hatch-description-editor" rows={16} value={draft} onChange={(e) => setDraft(e.target.value)} />
      )}
    </Card>
  );
}

/**
 * The questions on this issue that nobody has answered, each with a box to
 * answer it in.
 *
 * A box per question rather than one for the lot: an answer is bound to the
 * question it settles (CommentCreateRequest.answersId), which is what lets two
 * questions on one ticket be decided a day apart, and what makes "is this still
 * waiting" a fact rather than a reading of the thread.
 *
 * Threaded from the comments the page already has - see lib/questions.ts for
 * why this is not a second request.
 */
function Waiting({
  issueKey,
  comments,
  onAnswered,
  onError,
}: {
  issueKey: string;
  comments: Comment[];
  onAnswered: () => void;
  onError: (message: string) => void;
}) {
  const open = openQuestions(comments);
  if (open.length === 0) return null;

  return (
    <Card as="section" className="hatch-waiting">
      <h2 className="hatch-section-title">
        Waiting on you ({open.length})
      </h2>
      <p className="text-muted">
        {issueKey} will not be picked up again until these are answered.
      </p>

      <ul className="hatch-question-list">
        {open.map((thread) => (
          <Asked key={thread.question.id} issueKey={issueKey} question={thread.question} onAnswered={onAnswered} onError={onError} />
        ))}
      </ul>
    </Card>
  );
}

/** One question, and the answer being typed to it. */
function Asked({
  issueKey,
  question,
  onAnswered,
  onError,
}: {
  issueKey: string;
  question: Comment;
  onAnswered: () => void;
  onError: (message: string) => void;
}) {
  const [body, setBody] = useState('');
  const [saving, setSaving] = useState(false);

  async function submit() {
    setSaving(true);
    try {
      await addComment(issueKey, { body, kind: 'answer', answersId: question.id });
      setBody('');
      onAnswered();
    } catch (err) {
      onError(message(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <li className="hatch-question">
      <div className="hatch-comment-head">
        <strong>{question.author}</strong>
        <span className="text-muted">asked {new Date(question.createdAt).toLocaleString()}</span>
      </div>
      <div className="hatch-markdown" dangerouslySetInnerHTML={{ __html: renderMarkdown(question.body) }} />

      <div className="hatch-comment-box">
        <textarea
          rows={2}
          value={body}
          placeholder="The decision, in a sentence."
          onChange={(e) => setBody(e.target.value)}
        />
        <Button variant="primary" loading={saving} disabled={!body.trim()} onClick={() => void submit()}>
          Answer
        </Button>
      </div>
    </li>
  );
}

function Comments({
  issueKey,
  comments,
  onAdded,
  onError,
}: {
  issueKey: string;
  comments: Comment[];
  onAdded: () => void;
  onError: (message: string) => void;
}) {
  const [body, setBody] = useState('');
  const [saving, setSaving] = useState(false);

  async function submit() {
    setSaving(true);
    try {
      await addComment(issueKey, { body });
      setBody('');
      onAdded();
    } catch (err) {
      onError(message(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Card>
      <h2 className="hatch-section-title">Comments</h2>

      {comments.length === 0 && <p className="text-muted">Nothing said yet.</p>}

      <ul className="hatch-comments">
        {comments.map((comment) => (
          <li key={comment.id} className={`hatch-comment${comment.kind ? ` hatch-comment-${comment.kind}` : ''}`}>
            <div className="hatch-comment-head">
              <strong>{comment.author}</strong>
              {/* The thread still shows everything in the order it was said -
                  the card above is for acting, this is for reading back what
                  was decided and when. */}
              {comment.kind === 'question' && <Badge>asked</Badge>}
              {comment.kind === 'answer' && <Badge>answered</Badge>}
              <span className="text-muted">{new Date(comment.createdAt).toLocaleString()}</span>
            </div>
            <div className="hatch-markdown" dangerouslySetInnerHTML={{ __html: renderMarkdown(comment.body) }} />
          </li>
        ))}
      </ul>

      <div className="hatch-comment-box">
        <textarea rows={3} value={body} placeholder="Markdown, like everything else." onChange={(e) => setBody(e.target.value)} />
        <Button variant="primary" loading={saving} disabled={!body.trim()} onClick={() => void submit()}>
          Comment
        </Button>
      </div>
    </Card>
  );
}

/** The audit trail, collapsed. It is there to be looked up, not to be read. */
function EventTrail({ events }: { events: IssueEvent[] }) {
  return (
    <Card>
      <details className="hatch-events">
        <summary className="hatch-section-title">History ({events.length})</summary>
        <ul>
          {events.map((event) => (
            <li key={event.id} className="hatch-event">
              <span className="text-muted">{new Date(event.at).toLocaleString()}</span>
              <Badge>{event.kind.replace('_', ' ')}</Badge>
              <span>{event.actor}</span>
              <span className="text-muted">{describe(event)}</span>
            </li>
          ))}
        </ul>
      </details>
    </Card>
  );
}

/**
 * One line saying what an event did. Long values are cut rather than wrapped -
 * a description edit carries both whole texts in its payload, and the trail is
 * a list of what happened, not a diff viewer.
 */
function describe(event: IssueEvent): string {
  const { from, to } = event.payload ?? {};
  if (from === undefined && to === undefined) return '';
  return `${short(from)} → ${short(to)}`;
}

function short(value: unknown): string {
  if (value === null || value === undefined) return 'none';
  const text = String(value);
  return text.length > 60 ? `${text.slice(0, 60)}…` : text;
}
