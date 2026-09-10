import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Card, Field, PageHeader } from '@aerie/ui';
import {
  addComment,
  addDependency,
  clearClaim,
  createIssue,
  deleteIssue,
  getAssignees,
  getBoard,
  getComments,
  getEvents,
  getIssue,
  getIssuePlan,
  getNextWorkUnder,
  getWorkLog,
  patchIssue,
  patchIssuePlaybook,
  removeDependency,
  setAssignee,
  setExpedited,
} from '../api/client';
import { AssigneeField } from '../components/AssigneeField';
import { ExpediteControl } from '../components/ExpediteControl';
import { ClaimPanel } from '../components/ClaimPanel';
import { ClearClaimDialog } from '../components/ClearClaimDialog';
import { Choice } from '../components/Choice';
import { CloseSubtreeDialog } from '../components/CloseSubtreeDialog';
import { Command } from '../components/Command';
import { DescriptionEditor } from '../components/DescriptionEditor';
import { IssuePicker } from '../components/IssuePicker';
import { MomentChip } from '../components/MomentChip';
import { StatusMeter } from '../components/StatusMeter';
import { StatusPill } from '../components/StatusPill';
import { MomentField } from '../components/MomentField';
import { PullRequestLink } from '../components/PullRequestLink';
import { TypeBadge } from '../components/TypeBadge';
import { WorkLog } from '../components/WorkLog';
import { assigneeHint } from '../lib/assignee';
import { childTypes } from '../lib/childTypes';
import { statusVars } from '../lib/color';
import { closeOffer } from '../lib/closeSubtree';
import { dependencyCandidates } from '../lib/dependencies';
import { message } from '../lib/errors';
import { renderMarkdown } from '../lib/markdown';
import { waitingChild } from '../lib/next';
import { openQuestions } from '../lib/questions';
import { useAutoGrow } from '../lib/useAutoGrow';
import { useCloseSubtree } from '../lib/useCloseSubtree';
import { ISSUE_TYPES, LEGAL_PARENT_TYPES, PLAYBOOK_EFFORTS, PLAYBOOK_MODELS } from '../types';
import type {
  AssigneeDirectory,
  AssigneeRequest,
  Board,
  ChildRollup,
  Comment,
  Issue,
  IssueCard,
  IssueEvent,
  IssueRollup,
  IssueType,
  QuestionOption,
  Status,
  Work,
  WorkLog as WorkLogData,
} from '../types';

export function IssuePage() {
  const { key = '' } = useParams();
  const navigate = useNavigate();

  const [issue, setIssue] = useState<Issue | null>(null);
  const [board, setBoard] = useState<Board | null>(null);
  const [directory, setDirectory] = useState<AssigneeDirectory | null>(null);
  const [comments, setComments] = useState<Comment[]>([]);
  const [events, setEvents] = useState<IssueEvent[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [rollup, setRollup] = useState<IssueRollup | null>(null);
  const [rollupError, setRollupError] = useState<string | null>(null);
  const [next, setNext] = useState<NextAnswer | null>(null);
  const [workLog, setWorkLog] = useState<WorkLogData | null>(null);
  const [workLogError, setWorkLogError] = useState<string | null>(null);
  /* The speed bump in front of taking a ticket off a runner, and whether the
     clear is in flight. Two flags rather than one, because the dialog stays up
     while the request runs and says so on its own button. */
  const [clearingClaim, setClearingClaim] = useState(false);
  const [claimClearing, setClaimClearing] = useState(false);

  const load = useCallback(async () => {
    /* The fifth read, sent with the other four and awaited apart from them.
       The four are what the page is made of and share one failure; the rollup
       is one card on it, so its failure is caught here and handed to that card
       instead of blanking an issue somebody came here to read. Handlers are
       attached at the call, so a rejection is never loose. */
    const rolling = getIssuePlan(key).then(
      (loaded) => ({ loaded, failure: null as string | null }),
      (err: unknown) => ({ loaded: null, failure: message(err) }),
    );

    /* And the sixth, on the same terms: what an agent would pick up under this
       issue. Sent unconditionally rather than after the issue arrives and says
       whether it has children - an issue with none has an empty subtree, which
       the server answers with the 204 that means "nothing to do", and waiting
       for the first read to decide would put this line on the page a moment
       after somebody started reading it. */
    const asking = getNextWorkUnder(key).then(
      (work) => ({ work, error: null as string | null }),
      (err: unknown) => ({ work: null, error: message(err) }),
    );

    /* And the seventh, on the same terms as the rollup: what every session run
       against this issue and everything beneath it has cost. Its own failure,
       handed to its own section - a meter that could not be read must not blank
       an issue somebody came here to read. */
    const metering = getWorkLog(key).then(
      (loaded) => ({ loaded, failure: null as string | null }),
      (err: unknown) => ({ loaded: null, failure: message(err) }),
    );

    /* And the eighth: everybody this issue could belong to, and who is signed
       in. Its own failure and no error line of its own - a directory that could
       not be read leaves the field disabled with the assignee still drawn on
       it, which is the honest state of "I know whose this is and cannot offer
       to change it". */
    const whom = getAssignees().then(
      (loaded) => loaded,
      () => null,
    );

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

    setDirectory(await whom);

    const settled = await rolling;
    setRollup(settled.loaded);
    setRollupError(settled.failure);

    setNext(await asking);

    const metered = await metering;
    setWorkLog(metered.loaded);
    setWorkLogError(metered.failure);
  }, [key]);

  useEffect(() => {
    void load();
    const onFocus = () => void load();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [load]);

  /* Answers whether the patch went through. Every existing caller says
     `void save({ … })` and is unaffected; the one that asks is the status bar,
     because an offer to close a subtree must not follow a move the server
     refused. */
  const save = useCallback(
    async (patch: Parameters<typeof patchIssue>[1]): Promise<boolean> => {
      try {
        await patchIssue(key, patch);
        await load();
        return true;
      } catch (err) {
        setError(message(err));
        return false;
      }
    },
    [key, load],
  );

  /* The two the ordinary patch route does not carry. Its own call because it
     is its own endpoint: setting an override is closed to an API key, and that
     refusal is a property of the route rather than of a rule anybody is asked
     to follow. Otherwise exactly `save` - it re-reads, and a refusal lands in
     `error` above in the server's own words. */
  const savePlaybook = useCallback(
    async (patch: Parameters<typeof patchIssuePlaybook>[1]) => {
      try {
        await patchIssuePlaybook(key, patch);
        await load();
      } catch (err) {
        setError(message(err));
      }
    },
    [key, load],
  );

  /* Who owns it. Its own call for the reason `savePlaybook` is - it is its own
     endpoint, and writing one is closed to an API key - and otherwise exactly
     `save`: it re-reads, so the card, the hint and the directory's idea of who
     is signed in all redraw together, and a refusal lands in `error` above in
     the server's own words. */
  const saveAssignee = useCallback(
    async (request: AssigneeRequest) => {
      try {
        await setAssignee(key, request);
        await load();
      } catch (err) {
        setError(message(err));
      }
    },
    [key, load],
  );

  /* Whether this one goes first. Its own call for the reason `saveAssignee` is -
     its own endpoint, closed to an API key - and otherwise exactly `save`: it
     re-reads, so the control, the trail below and the board behind this page
     all redraw from the server's answer rather than from the assumption that
     the press worked. A refusal lands in `error` above in the server's own
     words, and the control goes back to saying what the issue still holds. */
  const saveExpedited = useCallback(
    async (expedited: boolean) => {
      try {
        await setExpedited(key, expedited);
        await load();
      } catch (err) {
        setError(message(err));
      }
    },
    [key, load],
  );

  /* Taking the ticket back off a runner. Its own call for the reason
     `saveAssignee` is - its own endpoint, closed to an API key - and otherwise
     exactly `save`: it re-reads, so the section, the card and the trail below
     all redraw together, and a refusal lands in `error` above in the server's
     own words.

     The dialog is shut either way, including on a refusal - the page's error
     line is behind the scrim, and an explanation nobody can read until they
     dismiss the thing covering it is not an explanation. */
  const clearTheClaim = useCallback(async () => {
    setClaimClearing(true);
    try {
      await clearClaim(key);
      await load();
    } catch (err) {
      setError(message(err));
    } finally {
      setClaimClearing(false);
      setClearingClaim(false);
    }
  }, [key, load]);

  /* An edge added or taken off. Its own call rather than a field on the patch
     for the reason `savePlaybook` is - it is its own endpoint - and otherwise
     exactly `save`: it re-reads, and a refusal lands in `error` above in the
     server's own words, which are the sentences naming which rule the edge
     broke. */
  const saveDependency = useCallback(
    async (write: () => Promise<unknown>) => {
      try {
        await write();
        await load();
      } catch (err) {
        setError(message(err));
      }
    },
    [load],
  );

  // Handed `load`, so a confirmed cascade re-reads the rollup too and the
  // progress meter counts the children that closed. Declared with the other
  // hooks, above the guards below, because the rules of hooks are an error here.
  const closing = useCloseSubtree(load);

  if (error && !issue) return <p className="text-danger">{error}</p>;
  if (!issue || !board) return <p className="text-muted">Loading…</p>;

  // The legal parents: same project, a type this issue may hang under, and
  // never itself. The server decides too - this only keeps the picker from
  // offering something it will refuse.
  const legal = LEGAL_PARENT_TYPES[issue.type];
  const parents = board.issues.filter(
    (i) => i.projectKey === issue.projectKey && i.key !== issue.key && legal.includes(i.type),
  );

  // What may be filed under this issue, read off the same table the server
  // refuses by - see lib/childTypes.ts. Empty on a task, which is what decides
  // both the composer and, with childKeys, whether the card is drawn at all.
  const filings = childTypes(issue.type);

  // Sitting in a column that means it shipped, so the due chip stops warning -
  // the same rule the board follows, for the same reason.
  const terminal = board.statuses.find((s) => s.id === issue.statusId)?.isTerminal ?? false;

  /* A const rather than a declaration, so it is written after the guards above
     and `issue` and `board` are the narrowed ones. */
  const move = async (statusId: number) => {
    // Computed before the patch, so it is the subtree the operator was looking
    // at when they pressed; asked after it, so a refused move asks nothing.
    const offer = closeOffer(board, key, issue.statusId, statusId);
    if (await save({ statusId })) closing.ask(offer);
  };

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
            <PullRequestLink url={issue.pullRequestUrl} />
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

      {/* Beside Waiting and for the same reason: something else is acting on
          this ticket right now, and that is worth knowing before pressing
          anything below. Draws nothing on the overwhelming majority of pages. */}
      <ClaimPanel issueKey={key} claim={issue.claim} onClear={() => setClearingClaim(true)} />

      <ClearClaimDialog
        issueKey={key}
        claim={clearingClaim ? issue.claim : null}
        busy={claimClearing}
        onConfirm={() => void clearTheClaim()}
        onClose={() => setClearingClaim(false)}
      />

      <StatusBar
        statuses={board.statuses}
        statusId={issue.statusId}
        onMove={(statusId) => void move(statusId)}
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

          {/* `as="div"`: Field wraps its children in a <label> for implicit
              association, and text inside a label joins the control's
              accessible name - with the popup inside it, the whole candidate
              list would be read out as the name of this control. The picker
              carries its own aria-label instead, which is why `label` is
              passed twice. The hint is unaffected: Field already draws it
              outside the label, for the same reason.

              The clear is still the empty string the API reads as "no parent"
              - see IssuePatchRequest - now the `— none —` row rather than an
              empty <option>. `save` is handed over rather than wrapped in
              `void`: the picker awaits it to know when the press is over, and
              `save` catches its own rejection and puts the server's sentence
              in `error` above. */}
          <Field label="Parent" as="div" hint={`A ${issue.type} hangs under ${legal.join(' or ')}.`}>
            <IssuePicker
              label="Parent"
              value={issue.parentKey}
              candidates={parents}
              emptyMessage={`Nothing in ${issue.projectKey} can be a parent of a ${issue.type} yet.`}
              onChange={(parentKey) => save({ parentKey })}
            />
          </Field>

          {/* `as="div"` for the reason the Parent field is - see there: a
              <label> wrapping a control with text in it swallows the
              accessible name, and the field carries its own aria-label. */}
          <Field label="Assignee" as="div" hint={assigneeHint(issue.assignee)}>
            <AssigneeField
              assignee={issue.assignee}
              directory={directory}
              onChange={(request) => void saveAssignee(request)}
            />
          </Field>

          {/* `as="div"` for the reason the fields above it are. The control
              draws the current state, so this field says whether the issue is
              expedited without anybody pressing anything - which is the point
              of it being here as well as on the card. */}
          <Field
            label="Expedite"
            as="div"
            hint="This one first: to the top of its column, and the first thing the dispatcher considers."
          >
            <ExpediteControl
              expedited={issue.expedited}
              directory={directory}
              onChange={(expedited) => void saveExpedited(expedited)}
            />
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

          {/* The playbook for a transition prices every ticket that makes it.
              These two say this one is different, and they beat every playbook
              that could speak for it - not one transition's worth. `— playbook —`
              is the empty string the route reads as "clear it", and is where
              every issue on the board starts.

              Drawn from the constants the Playbooks page draws from, so what an
              issue may be set to and what a playbook may be set to cannot drift
              apart. A pinned `claude-…` id somebody set through the API is
              offered back rather than swapped for an alias - that is Choice's
              own doing. */}
          <Field label="Model" hint="What an agent dispatched for this issue runs on.">
            <Choice
              value={issue.modelOverride ?? ''}
              options={PLAYBOOK_MODELS}
              placeholder="— playbook —"
              onChange={(model) => void savePlaybook({ model })}
            />
          </Field>

          <Field label="Effort" hint="How hard it thinks. Empty means the playbook decides.">
            <Choice
              value={issue.effortOverride ?? ''}
              options={PLAYBOOK_EFFORTS}
              placeholder="— playbook —"
              onChange={(effort) => void savePlaybook({ effort })}
            />
          </Field>
        </div>
      </Card>

      <Dependencies
        issue={issue}
        board={board}
        onAdd={(dependsOnKey) => saveDependency(() => addDependency(key, { dependsOnKey }))}
        onRemove={(dependsOnKey) => saveDependency(() => removeDependency(key, dependsOnKey))}
      />

      <Description issue={issue} onSave={(description) => save({ description })} />

      {/* Drawn where something may be filed under this issue, and where
          something already is. The second arm is not redundant: a retype does
          not re-check what already hangs below (StageEditAsync), so an epic
          with stories can be turned into a task, and that card must keep its
          list.

          Inside it, the meter and the list are gated on childKeys, which
          arrived with the issue, rather than on the rollup: the card's shape is
          settled on the first paint instead of rearranging itself under the
          reader a moment later. A task's meter, and an epic nobody has put
          anything under, could read 0% or 100% and nothing else - which says
          less than the status band already above it. */}
      {(filings.length > 0 || issue.childKeys.length > 0) && (
        <Progress
          rollup={rollup}
          error={rollupError}
          statuses={board.statuses}
          next={next}
          hasChildren={issue.childKeys.length > 0}
          projectId={issue.projectId}
          parentKey={issue.key}
          types={filings}
          onFiled={() => void load()}
        />
      )}

      <Comments issueKey={key} comments={comments} onAdded={() => void load()} onError={setError} />

      {/* Between the thread and the trail, and visibly part of neither: a
          comment is somebody talking, an event is something happening, and this
          is the meter. */}
      <WorkLog log={workLog} error={workLogError} />

      <EventTrail events={events} />

      <CloseSubtreeDialog
        offer={closing.offer}
        busy={closing.busy}
        error={closing.error}
        failures={closing.failures}
        onConfirm={closing.confirm}
        onClose={closing.close}
      />
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
 * rules on purpose (docs/hatch.md, "Non-goals") - any status to any status,
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

/**
 * What this issue waits on, and what waits on it.
 *
 * Two lists of the same edges read from the two ends. Only the first is
 * editable here, because an edge is owned by the issue that waits: taking one
 * off from the blocker's page would be one issue deciding another issue's
 * order.
 *
 * A dependency gates one thing - the move into the column where the code gets
 * written - and gates it until the issue it names is in a terminal column. So
 * a blocker sitting in review still blocks, which is why each row wears the
 * column it is in: that pill is the answer to "why is this still waiting".
 */
function Dependencies({
  issue,
  board,
  onAdd,
  onRemove,
}: {
  issue: Issue;
  board: Board;
  onAdd: (dependsOnKey: string) => Promise<unknown>;
  onRemove: (dependsOnKey: string) => Promise<unknown>;
}) {
  const candidates = dependencyCandidates(board.issues, issue.key, issue.dependsOnKeys);

  return (
    <Card>
      <h2 className="hatch-section-title">Depends on</h2>
      <p className="hatch-section-hint text-muted">
        Done before this is implemented. Everything to the left of that still moves.
      </p>

      {issue.dependsOnKeys.length === 0 ? (
        <p className="text-muted">&mdash;</p>
      ) : (
        <ul className="hatch-progress-list">
          {issue.dependsOnKeys.map((key) => (
            <DependencyRow key={key} issueKey={key} board={board} onRemove={onRemove} />
          ))}
        </ul>
      )}

      {/* `as="div"` for the reason the Parent field is - see there. `allowNone`
          off and a placeholder in its place: taking a row here adds an edge
          rather than replacing the one value a field holds, so there is
          nothing for a clear row to clear, and removing is the button on the
          row itself. */}
      <div className="hatch-depends-add">
        <Field label="Add a dependency" as="div">
          <IssuePicker
            label="Add a dependency"
            value={null}
            allowNone={false}
            placeholder="Add a dependency…"
            candidates={candidates}
            emptyMessage="Nothing else on the board can be depended on."
            onChange={onAdd}
          />
        </Field>
      </div>

      <h2 className="hatch-section-title">Blocks</h2>
      <p className="hatch-section-hint text-muted">
        Waiting on this one. Taken off from their own pages &mdash; an edge belongs to the issue that
        waits.
      </p>

      {issue.dependentKeys.length === 0 ? (
        <p className="text-muted">&mdash;</p>
      ) : (
        <ul className="hatch-progress-list">
          {issue.dependentKeys.map((key) => (
            <DependencyRow key={key} issueKey={key} board={board} />
          ))}
        </ul>
      )}
    </Card>
  );
}

/**
 * One edge, drawn from the board rather than fetched: the key, what it is, its
 * title and the column it is in.
 *
 * An issue the board does not carry is drawn as its key alone rather than
 * dropped. A key with nothing behind it is a thing somebody has to be able to
 * see in order to take it off; a silently missing row is not.
 */
function DependencyRow({
  issueKey,
  board,
  onRemove,
}: {
  issueKey: string;
  board: Board;
  /** Absent on the Blocks list, which is read-only from this page. */
  onRemove?: (dependsOnKey: string) => Promise<unknown>;
}) {
  const [removing, setRemoving] = useState(false);
  const card: IssueCard | undefined = board.issues.find((i) => i.key === issueKey);
  const status = board.statuses.find((s) => s.id === card?.statusId);

  return (
    <li className="hatch-progress-row">
      <Link to={`/issues/${issueKey}`} className="hatch-plan-key">
        {issueKey}
      </Link>
      {card && <TypeBadge type={card.type} />}
      <span className="hatch-progress-title">{card?.title ?? ''}</span>
      {status && <StatusPill status={status} />}
      {onRemove && (
        <Button
          className="hatch-depends-remove"
          loading={removing}
          aria-label={`Stop waiting on ${issueKey}`}
          onClick={() => {
            setRemoving(true);
            void onRemove(issueKey).finally(() => setRemoving(false));
          }}
        >
          Remove
        </Button>
      )}
    </li>
  );
}

/**
 * How far along this issue is, and what it is made of.
 *
 * The card an epic or a story grows the moment something is filed under it: the
 * whole subtree's meter across the top, and a line per direct child beneath.
 *
 * It replaces the list of child keys this card used to be rather than sitting
 * beside it. Two lists of the same children on one page is one list going
 * stale, and the keys are still here - each row starts with one.
 */
function Progress({
  rollup,
  error,
  statuses,
  next,
  hasChildren,
  projectId,
  parentKey,
  types,
  onFiled,
}: {
  rollup: IssueRollup | null;
  error: string | null;
  statuses: Status[];
  next: NextAnswer | null;
  /** Whether anything is filed under this issue, off the issue rather than off
      the rollup, so the card does not change shape when the fifth read lands. */
  hasChildren: boolean;
  projectId: number;
  parentKey: string;
  /** What may be filed here. Empty draws no composer - a task takes nothing. */
  types: IssueType[];
  onFiled: () => void;
}) {
  return (
    <Card>
      <h2 className="hatch-section-title">Progress</h2>

      {hasChildren && (
        <>
          {/* Said here, where the thing that failed was going to be. The rest of
              the page is already readable without it, so this is not the page's
              error - and it is not nothing, either, which is what a card that
              quietly stayed empty would be. */}
          {error && <p className="text-danger">{error}</p>}
          {!rollup && !error && <p className="text-muted">Loading…</p>}

          {rollup && (
            <>
              <StatusMeter rollup={rollup.rollup} statuses={statuses} size="lg" counts />

              <Next answer={next} rollup={rollup} />

              {/* Rank order, as the server sent them - the order they sit in on
                  the board's column, so the two screens agree about what is
                  next. */}
              <ul className="hatch-progress-list">
                {rollup.children.map((child) => (
                  <ChildRow key={child.issue.key} child={child} statuses={statuses} />
                ))}
              </ul>
            </>
          )}
        </>
      )}

      {/* Last in the card, and not waiting on the rollup: it is drawn from the
          issue, which has already arrived. */}
      {types.length > 0 && (
        <ChildComposer projectId={projectId} parentKey={parentKey} types={types} onFiled={onFiled} />
      )}
    </Card>
  );
}

/**
 * Filing the next child where the children are listed.
 *
 * A type and a title and nothing else. A description and dates belong to the
 * child and are set on its own page; asking for them here would make filing
 * three stories under an epic three visits to a dialog, which is the thing
 * this card exists to stop.
 *
 * What it may offer is not decided here - childTypes reads the same table the
 * server refuses by, so the picker cannot come to disagree with the refusal.
 */
function ChildComposer({
  projectId,
  parentKey,
  types,
  onFiled,
}: {
  projectId: number;
  parentKey: string;
  types: IssueType[];
  onFiled: () => void;
}) {
  const [picked, setPicked] = useState<IssueType | null>(null);
  const [title, setTitle] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const box = useRef<HTMLInputElement>(null);

  // Null until somebody chooses, so the first legal type is the default without
  // an effect to set it - and clamped on the way out, because the issue's own
  // type is editable on this page: a picker still holding "story" after the
  // epic above it became a story would offer what the server refuses.
  const chosen = picked && types.includes(picked) ? picked : types[0];

  async function submit() {
    setSaving(true);
    try {
      await createIssue({ projectId, type: chosen, title: title.trim(), parentKey });
      setTitle('');
      setError(null);
      box.current?.focus();
      onFiled();
    } catch (err) {
      // The title is deliberately left where it was typed - the refusal is
      // usually about the type, and retyping the title to try again is a tax.
      setError(message(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="hatch-inline-form hatch-child-composer" role="group" aria-label="File a child issue">
      <Field label="Type">
        <select value={chosen} onChange={(e) => setPicked(e.target.value as IssueType)}>
          {types.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </Field>

      <Field label="Title" className="hatch-child-composer-title">
        <input
          ref={box}
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          // Enter files it. A title is one line, so there is no newline for
          // Enter to be taking away - unlike the comment boxes on this page,
          // which need meta+Enter for exactly that reason.
          onKeyDown={(e) => {
            if (e.key === 'Enter' && title.trim() && !saving) void submit();
          }}
        />
      </Field>

      <Button variant="primary" loading={saving} disabled={!title.trim()} onClick={() => void submit()}>
        File it
      </Button>

      {/* Said here, under the box that caused it, in the sentence the server
          wrote - see failureMessage in api/client.ts. */}
      {error && <p className="text-danger">{error}</p>}
    </div>
  );
}

/** The scoped `work/next`, settled: the issue an agent would take under this
    one, or the sentence saying why the ask itself did not go through. Null
    where neither has arrived. */
interface NextAnswer {
  work: Work | null;
  error: string | null;
}

/**
 * What an agent would pick up under this issue, and the one command that sets
 * it going.
 *
 * The last sentence of the loop this tracker exists for: find the project worth
 * furthering, find the next actionable story under it, and move it forward. The
 * Plan page answers the first; this answers the second, on the page of the epic
 * somebody has already chosen - once, rather than on every card of a screen
 * holding twenty-eight of them.
 *
 * Which issue is next is not decided here and could not be: the rule is the
 * server's (WorkController), and a copy of it in a browser would disagree with
 * the CLI the first time a column was renamed. This draws the answer.
 */
function Next({ answer, rollup }: { answer: NextAnswer | null; rollup: IssueRollup }) {
  // Not back yet. Said with nothing rather than with a second "Loading…" under
  // a meter that has already painted - the card is readable, and this line
  // arriving a moment later is what it is.
  if (answer === null) return null;

  // The ask failed while the rollup did not. Muted rather than red: the meter
  // above is the card's subject and it is fine, and this is one line of it
  // that could not be filled in.
  if (answer.error !== null) {
    return <p className="hatch-next-none text-muted">Could not ask what is next here: {answer.error}</p>;
  }

  if (answer.work !== null) {
    const found = answer.work.issue;
    return (
      <p className="hatch-next">
        <span className="hatch-next-label">Next</span>
        <Link to={`/issues/${found.key}`} className="hatch-plan-key">
          {found.key}
        </Link>
        <span className="hatch-next-title">— {found.title}</span>
        <Command command={`hatch work ${found.key}`} />
      </p>
    );
  }

  // A 204: nothing under here is an agent's to move. That has several causes -
  // it is all shipped, it is all in review, it is all waiting on a date - and
  // only one of them is worth naming, because only one of them is somebody's
  // to fix from this page.
  const waiting = rollup.rollup.waiting;
  const holder = waitingChild(rollup);

  return (
    <p className="hatch-next-none text-muted">
      Nothing under this is an agent&rsquo;s to move.{' '}
      {waiting > 0 && (
        <>
          {waiting} question{waiting === 1 ? ' is' : 's are'} waiting on a person
          {holder && (
            <>
              , on{' '}
              <Link to={`/issues/${holder}`} className="hatch-plan-key">
                {holder}
              </Link>
            </>
          )}
          .
        </>
      )}
    </p>
  );
}

/** One direct child: what it is, and either how far along it is or where it sits. */
function ChildRow({ child, statuses }: { child: ChildRollup; statuses: Status[] }) {
  const status = statuses.find((s) => s.id === child.issue.statusId);
  const waiting = child.rollup.waiting;

  return (
    <li className="hatch-progress-row">
      <Link to={`/issues/${child.issue.key}`} className="hatch-plan-key">
        {child.issue.key}
      </Link>
      <TypeBadge type={child.issue.type} />
      <span className="hatch-progress-title">{child.issue.title}</span>

      {/* Somebody owes an answer at or below this child, which is why the bar
          above has stopped. Worn by the row rather than printed inside the
          meter's counts, so it is in the same place on a leaf - which has no
          meter to put it in and is exactly the row most likely to be the one
          holding everything up. */}
      {waiting > 0 && (
        <span
          className="hatch-meter-waiting"
          title={`${waiting} unanswered question${waiting === 1 ? '' : 's'} at or below ${child.issue.key}`}
        >
          waiting
        </span>
      )}

      {/* isLeaf decides, not the shape of the numbers: a story with one task
          and a task with none both roll up to a single leaf, and only one of
          them has a bar worth drawing. */}
      {child.isLeaf ? (
        status ? (
          <StatusPill status={status} />
        ) : (
          <span className="text-muted">unknown</span>
        )
      ) : (
        <StatusMeter rollup={child.rollup} statuses={statuses} size="sm" counts waiting={false} />
      )}
    </li>
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
function Description({ issue, onSave }: { issue: Issue; onSave: (description: string) => Promise<boolean> }) {
  return (
    <Card>
      <DescriptionEditor
        title={<h2 className="hatch-section-title">Description</h2>}
        value={issue.description}
        onSave={onSave}
        editorClassName="hatch-grows"
      />
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

/**
 * One question, and the answer being typed to it.
 *
 * Where the question offered choices, they are the interface: pressing one puts
 * its label in the box, and the box is still there for the case the asker did
 * not anticipate. That ordering is the whole point - a decision between named
 * things should cost one press, and the free text should be the escape hatch
 * rather than the only door.
 */
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

  // Derived rather than held beside `body`: the highlight is "the box says
  // exactly this option", and a second piece of state saying the same thing is
  // a second thing that can come to disagree with the first.
  const chosen = question.options?.find((o) => o.label === body)?.label ?? null;

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

      <div
        className="hatch-markdown hatch-question-body"
        dangerouslySetInnerHTML={{ __html: renderMarkdown(question.body) }}
      />

      {question.options && (
        <QuestionOptions options={question.options} chosen={chosen} onChoose={(o) => setBody(o.label)} />
      )}

      <div className="hatch-comment-box">
        <textarea
          rows={2}
          value={body}
          placeholder={question.options ? 'Or say something else.' : 'The decision, in a sentence.'}
          onChange={(e) => setBody(e.target.value)}
          // Meta/Ctrl+Enter sends, the convention every comment box in the
          // world shares. A bare Enter has to stay a newline - an answer with a
          // caveat under it is a good answer.
          onKeyDown={(e) => {
            if (e.key === 'Enter' && (e.metaKey || e.ctrlKey) && body.trim() && !saving) void submit();
          }}
        />
        <Button variant="primary" loading={saving} disabled={!body.trim()} onClick={() => void submit()}>
          Answer
        </Button>
      </div>
    </li>
  );
}

/**
 * The answers a question offers.
 *
 * One component for both places they appear, because they are the same list
 * read for different reasons. With `onChoose` they are buttons, in the card
 * that is asking for a decision. Without it they are the record of what the
 * alternatives were, under the question in the thread - which is half of what
 * makes a decision legible later, and would be lost if the options were only
 * ever drawn while somebody could still press them.
 */
function QuestionOptions({
  options,
  chosen,
  onChoose,
}: {
  options: QuestionOption[];
  chosen?: string | null;
  onChoose?: (option: QuestionOption) => void;
}) {
  return (
    <ul className={`hatch-options${onChoose ? '' : ' static'}`}>
      {options.map((option) => {
        const face = (
          <>
            <span className="hatch-option-label">
              {option.label}
              {option.recommended && <span className="hatch-option-recommended">recommended</span>}
            </span>
            {option.detail && <span className="hatch-option-detail">{option.detail}</span>}
          </>
        );

        return (
          <li key={option.label}>
            {onChoose ? (
              <button
                type="button"
                className={`hatch-option${chosen === option.label ? ' chosen' : ''}`}
                aria-pressed={chosen === option.label}
                onClick={() => onChoose(option)}
              >
                {face}
              </button>
            ) : (
              <div className="hatch-option">{face}</div>
            )}
          </li>
        );
      })}
    </ul>
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
  const box = useAutoGrow(body);

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
            <div
              className={`hatch-markdown${comment.kind === 'question' ? ' hatch-question-body' : ''}`}
              dangerouslySetInnerHTML={{ __html: renderMarkdown(comment.body) }}
            />
            {comment.options && <QuestionOptions options={comment.options} />}
          </li>
        ))}
      </ul>

      <div className="hatch-comment-box">
        <textarea
          ref={box}
          className="hatch-grows"
          rows={3}
          value={body}
          placeholder="Markdown, like everything else."
          onChange={(e) => setBody(e.target.value)}
        />
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
              <Badge>{event.kind.replaceAll('_', ' ')}</Badge>
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
