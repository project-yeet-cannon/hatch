/* The wire shapes, mirroring src/Aerie.Api/Modules/Hatch/Dtos.cs.

   Hand-written rather than generated, like every other app here. The one rule
   that keeps that honest: a field added there and not here is invisible, so
   these records are the place to look first when a value arrives undefined. */

export type IssueType = 'epic' | 'story' | 'task' | 'bug';

/** The four types, in the order a picker offers them. Mirrors EfHatchIssue.Types. */
export const ISSUE_TYPES: IssueType[] = ['epic', 'story', 'task', 'bug'];

/** Which parents each type may take - mirrors EfHatchIssue.LegalParentTypes.
    Duplicated here to filter the parent picker; the server is still the one
    that decides, and a disagreement shows up as a refusal with a reason. */
export const LEGAL_PARENT_TYPES: Record<IssueType, IssueType[]> = {
  epic: ['epic'],
  story: ['epic'],
  task: ['story', 'bug'],
  bug: ['epic', 'story'],
};

export interface Project {
  id: number;
  key: string;
  name: string;
  issueCount: number;
  createdAt: string;
}

export interface Status {
  id: number;
  name: string;
  sortOrder: number;
  isTerminal: boolean;
  /** `#rrggbb`, lower case. What the column, the drag feedback and the issue
      page's status pill are all painted from - see lib/color.ts. */
  color: string;
}

/** A card on the board. No description and no comments - see IssueCardDto. */
export interface IssueCard {
  key: string;
  projectKey: string;
  type: IssueType;
  title: string;
  statusId: number;
  rank: number;
  parentKey: string | null;
  /** When it becomes workable, or null if it always was. See `Moment` in lib/schedule.ts. */
  readyAt: string | null;
  /** When it is owed, or null. Same two forms as `readyAt`. */
  dueAt: string | null;
  /** Questions on this issue nobody has answered. Non-zero means it is waiting
      on a person, and the board says so - see IssueCardDto.OpenQuestions. */
  openQuestions: number;
  /** The lease a running dispatcher holds on this issue, or null. */
  claim: IssueClaim | null;
}

/** The lease a running dispatcher holds on an issue - see IssueClaimDto.
    Null on the issue where nothing holds it, and null where the claim has
    expired: the server does that arithmetic, so nothing here has to.

    There is no token, and there is not meant to be one. The token is the
    capability a heartbeat and a release present, and a board read that carried
    it would let anybody holding a board read steal a lease. */
export interface IssueClaim {
  claimedBy: string;
  /** The checkout holding it - `host:/path/to/checkout`, as the runner names itself. */
  runner: string;
  claimedAt: string;
  /** When the holder was last heard from. The lease is over when this is older than the TTL. */
  heartbeatAt: string;
  /** A line the holder is carrying: what it is doing right now, or null if it has not said. */
  chatter: string | null;
  chatterAt: string | null;
}

export interface Issue {
  key: string;
  projectId: number;
  projectKey: string;
  type: IssueType;
  title: string;
  description: string;
  statusId: number;
  rank: number;
  parentKey: string | null;
  childKeys: string[];
  /** What must be done before this is implemented, in key order. An edge is
      satisfied only once the issue it names is in a terminal column, so a
      blocker sitting in review still blocks. */
  dependsOnKeys: string[];
  /** The issues waiting on this one - the same edges read backwards. Not
      editable from this issue's page: an edge is owned by the issue that
      waits. */
  dependentKeys: string[];
  readyAt: string | null;
  dueAt: string | null;
  /** Where the work is being reviewed, or null while it is nowhere. An absolute
      http(s) URL - see EfHatchIssue.PullRequestUrl for why it is one and not a
      list of them. */
  pullRequestUrl: string | null;
  /** The model every agent increment dispatched for this issue runs on, or null
      for whatever the playbook for its next move names. One of PLAYBOOK_MODELS,
      or a pinned `claude-…` id somebody set through the API. */
  modelOverride: string | null;
  /** The thinking budget those increments run at, or null for the playbook's.
      Independent of `modelOverride`: an issue may carry either, both or
      neither. One of PLAYBOOK_EFFORTS. */
  effortOverride: string | null;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
  /** The lease a running dispatcher holds on this issue, or null - the same
      shape the card carries, and null once it has expired. */
  claim: IssueClaim | null;
}

/** An ordinary note, a question that needs deciding, or the answer to one.
    Mirrors EfHatchComment.Kind; the empty string is a note. */
export type CommentKind = '' | 'question' | 'answer';

/** One answer a question offers up front. Mirrors QuestionOptionDto. */
export interface QuestionOption {
  /** The choice as it will be said, and what an answer's body becomes when it is taken. */
  label: string;
  /** What taking it means and what it costs. Absent on a choice that explains itself. */
  detail: string | null;
  /** The one the asker would take. At most one per question. */
  recommended: boolean;
}

export interface Comment {
  id: number;
  author: string;
  body: string;
  kind: CommentKind;
  /** The question this answers, on the same issue. Null on everything else. */
  answersId: number | null;
  /** The answers a question offers, or null on one asked in prose. */
  options: QuestionOption[] | null;
  createdAt: string;
}

/** A question with whatever has been said back to it. Empty `answers` is what
    "open" means - there is no second flag saying so. See QuestionDto. */
export interface Question {
  id: number;
  issueKey: string;
  issueTitle: string;
  body: string;
  askedBy: string;
  askedAt: string;
  options: QuestionOption[] | null;
  answers: Comment[];
}

export type IssueEventKind =
  | 'created'
  | 'retitled'
  | 'redescribed'
  | 'retyped'
  | 'status_changed'
  | 'parent_changed'
  | 'ready_changed'
  | 'due_changed'
  | 'pull_request_changed'
  | 'model_override_changed'
  | 'effort_override_changed'
  | 'dependency_added'
  | 'dependency_removed'
  | 'claim_taken'
  | 'claim_released'
  | 'claim_cleared'
  | 'commented'
  | 'asked'
  | 'answered'
  | 'imported';

export interface IssueEvent {
  id: number;
  actor: string;
  kind: IssueEventKind;
  /** `{ from, to }` for an edit, a filename for an import, absent for a comment. */
  payload: { from?: unknown; to?: unknown; [key: string]: unknown } | null;
  at: string;
}

export interface Board {
  statuses: Status[];
  issues: IssueCard[];
}

// ---- The importer ----

/** How far along an imported node is, before the board matches it to a column.
    Mirrors PlanState in Dtos.cs; the names are the enum's, serialized as
    strings by the API's JsonStringEnumConverter. */
export type PlanState = 'Todo' | 'InProgress' | 'Done';

export interface ParsedTask {
  title: string;
  description: string;
  state: PlanState;
}

export interface ParsedStory {
  title: string;
  description: string;
  state: PlanState;
  tasks: ParsedTask[];
}

/** One uploaded plan, as the issues it would become. Handed back by the
    preview and handed in again unchanged, so what was approved on screen is
    what gets written. */
export interface ParsedEpic {
  filename: string;
  title: string;
  description: string;
  state: PlanState;
  stories: ParsedStory[];
}

/** One plan typed straight into the page instead of uploaded. The title plays
    the part a filename plays for an upload: the provenance every issue carries,
    and the epic's title when the body has no `#` heading of its own. */
export interface PastedPlan {
  title: string;
  body: string;
}

export interface ImportRequest {
  projectId: number;
  docs: ParsedEpic[];
}

export interface ImportedEpic {
  filename: string;
  key: string;
  title: string;
  storyCount: number;
  taskCount: number;
}

export interface ImportResult {
  epics: ImportedEpic[];
  issueCount: number;
}

// ---- Requests ----

export interface ProjectCreateRequest {
  key: string;
  name: string;
}

/** Both optional, and `key` is the expensive one: it rekeys every issue in the
    project and leaves every AER-12 written elsewhere pointing at nothing. The
    speed bump in front of it is ProjectsPage's, not the API's. */
export interface ProjectPatchRequest {
  name?: string | null;
  key?: string | null;
}

export interface StatusCreateRequest {
  name: string;
  sortOrder?: number | null;
  isTerminal?: boolean | null;
  color?: string | null;
}

export interface StatusPatchRequest {
  name?: string | null;
  sortOrder?: number | null;
  isTerminal?: boolean | null;
  color?: string | null;
}

export interface IssueCreateRequest {
  projectId: number;
  type: IssueType;
  title: string;
  description?: string | null;
  parentKey?: string | null;
  readyAt?: string | null;
  dueAt?: string | null;
}

/** Null leaves a field alone. An empty `parentKey`, `readyAt`, `dueAt` or
    `pullRequestUrl` clears it - see IssuePatchRequest in Dtos.cs for why the
    empty string carries that meaning. */
export interface IssuePatchRequest {
  title?: string | null;
  description?: string | null;
  type?: IssueType | null;
  statusId?: number | null;
  parentKey?: string | null;
  readyAt?: string | null;
  dueAt?: string | null;
  /** An absolute http(s) URL, or `''` to take the issue off the one it holds.
      Anything else is refused with a sentence - the field's only job is to be
      clicked. */
  pullRequestUrl?: string | null;
}

/** What one issue overrides its playbooks with. Null leaves a field alone and
    `''` hands it back to the playbook, as everywhere else in Hatch.

    Its own request because it is its own route: setting one is closed to an API
    key, since an override is a playbook's power routed through another table -
    see IssuePlaybookController. */
export interface IssuePlaybookRequest {
  model?: string | null;
  effort?: string | null;
}

/** One edge: this issue waits on `dependsOnKey`. Open to an API key, unlike
    IssuePlaybookRequest - an edge is a statement about the work rather than
    about an agent's budget. See IssueDependenciesController. */
export interface IssueDependencyRequest {
  dependsOnKey: string;
}

/** A filter, as the search endpoint reads it. Every field is optional and they
    combine with AND; `parentKey: ''` is the one that cannot be said any other
    way - "no parent at all". */
export interface IssueSearch {
  projectId?: number | null;
  type?: IssueType | null;
  statusId?: number | null;
  parentKey?: string | null;
  ancestorKey?: string | null;
  text?: string | null;
}

/** One edit for many issues. Null leaves a field alone and `''` clears it, the
    same way a single-issue patch reads them. Title and description are
    deliberately absent: they describe one issue. */
export interface IssueBulkEditRequest {
  keys: string[];
  type?: IssueType | null;
  statusId?: number | null;
  parentKey?: string | null;
  readyAt?: string | null;
  dueAt?: string | null;
}

export interface IssueBulkFailure {
  key: string;
  reason: string;
}

export interface IssueBulkResult {
  /** The keys that actually moved. */
  changed: string[];
  /** Keys that matched but already held every named value - re-applying an edit is not an edit. */
  unchanged: string[];
  failures: IssueBulkFailure[];
}

export interface IssueMoveRequest {
  statusId: number;
  afterKey?: string | null;
  beforeKey?: string | null;
}

export interface CommentCreateRequest {
  body: string;
  /** Omitted by everything that just wants to say something. */
  kind?: CommentKind;
  /** Required on an answer, and refused on anything else. */
  answersId?: number;
  /** Offered answers, on a question only. */
  options?: QuestionOption[];
}

// ---- Playbooks ----

/** What the CLI's `--model` takes. Aliases rather than pinned ids, because a
    playbook says "the big one" and should still mean it a year from now. */
export type PlaybookModel = 'haiku' | 'sonnet' | 'opus' | 'fable';

export const PLAYBOOK_MODELS: PlaybookModel[] = ['haiku', 'sonnet', 'opus', 'fable'];

/** What a playbook gets when it is created without one said. Mirrors
    EfHatchPlaybook.DefaultModel, so the form shows what the server would have
    chosen rather than a different guess of its own. */
export const PLAYBOOK_MODEL_DEFAULT: PlaybookModel = 'sonnet';

/** What the CLI's `--effort` takes, cheapest first. */
export type PlaybookEffort = 'low' | 'medium' | 'high' | 'xhigh' | 'max';

export const PLAYBOOK_EFFORTS: PlaybookEffort[] = ['low', 'medium', 'high', 'xhigh', 'max'];

/** Mirrors EfHatchPlaybook.DefaultEffort - see PLAYBOOK_MODEL_DEFAULT. */
export const PLAYBOOK_EFFORT_DEFAULT: PlaybookEffort = 'medium';

/** One row of the matrix: a transition, the types it speaks for, and what an
    agent making that move is told and spent on. Mirrors PlaybookDto. */
export interface Playbook {
  id: number;
  fromStatusId: number;
  fromStatusName: string;
  toStatusId: number;
  toStatusName: string;
  /** Empty means every type. */
  types: IssueType[];
  prompt: string;
  /** An alias, or a pinned `claude-…` name an operator typed by hand. */
  model: string;
  effort: string;
  updatedAt: string;
}

export interface PlaybookCreateRequest {
  fromStatusId: number;
  toStatusId: number;
  types?: IssueType[];
  prompt: string;
  model?: string;
  effort?: string;
}

/** Null or absent leaves a field alone, as everywhere else in Hatch. */
export interface PlaybookPatchRequest {
  fromStatusId?: number;
  toStatusId?: number;
  types?: IssueType[];
  prompt?: string;
  model?: string;
  effort?: string;
}

// ---- Work ----

/** What an agent should do next and how, answered in one request. Every part
    of it is decided on the server - which issue, which column it is headed
    for, whether it may go there at all, and which playbook speaks for the move
    - so that this page and `hatch.sh` never disagree. Mirrors WorkDto. */
export interface Work {
  issue: Issue;
  fromStatus: Status;
  /** The column to the right, or null at the end of the board. */
  toStatus: Status | null;
  /** What the session would be told, and what it may spend. Null when no row
      covers the move. */
  playbook: Playbook | null;
  children: IssueCard[];
  /** Both halves: the open ones say why it is not dispatched, the answered
      ones are what a session would be dispatched knowing. */
  questions: Question[];
  /** Why an agent should not be spawned at this issue, or null when one
      should. A sentence, meant to be printed as it is. */
  blocked: string | null;
}

/** One column's share of a subtree: how many of its leaves sit there. A status
    no leaf is in is absent, not zero - the column list is already held here.
    Mirrors RollupSliceDto. */
export interface RollupSlice {
  statusId: number;
  count: number;
}

/** What a subtree adds up to, computed on the server so this page, the CLI and
    anything holding an API key read the same number. The unit is the leaf - an
    issue with no children - and a childless issue counts as one leaf in its own
    column. Mirrors RollupDto. */
export interface Rollup {
  /** What `slices` sums to. */
  leaves: number;
  /** Leaves in a terminal column: the numerator of "how far along is this". */
  done: number;
  /** Open questions on this issue and everything below it, at any depth.
      Non-zero means it is blocked on a person rather than on an agent. */
  waiting: number;
  /** Board order (`sortOrder`, then id), empty columns absent. */
  slices: RollupSlice[];
}

/** One direct child, with its own rollup. Mirrors ChildRollupDto. */
export interface ChildRollup {
  issue: IssueCard;
  /** No children of its own - draw a status pill rather than a bar. Carried
      rather than inferred from `leaves === 1`, because a story with a single
      task and a task with none are not the same thing. */
  isLeaf: boolean;
  rollup: Rollup;
}

/** One subtree and the row under it: the issue page's Progress card. Mirrors
    IssueRollupDto. */
export interface IssueRollup {
  key: string;
  rollup: Rollup;
  children: ChildRollup[];
}

/** One epic on the Plan page: the card, what everything beneath it adds up to,
    and the epics beneath it drawn the same way. Mirrors PlanEntryDto. */
export interface PlanEntry {
  issue: IssueCard;
  isLeaf: boolean;
  /** The whole subtree, not only the epics in `children` - every story, task
      and bug under it at any depth. */
  rollup: Rollup;
  /** The epics below this one, each appearing exactly here and not again at the
      top level, so the tree is drawn once. Ordered by key. */
  children: PlanEntry[];
}

/** The landscape in one request: every epic and what it adds up to. Mirrors
    PlanDto. */
export interface Plan {
  /** The epics with no parent, each carrying the epics beneath it. Ordered by
      key; whichever order the page wants is the page's to apply. */
  epics: PlanEntry[];
  /** The work hanging under no epic at all - the leaves below every root issue
      that is not an epic, and `leaves: 0` when there is none. It is here so the
      Plan page cannot quietly hide half the tracker from an operator who filed
      a story without a parent. */
  loose: Rollup;
}

// ---- The battery ----

/** One limit window as the server describes it. Mirrors UtilizationLimit. */
export interface UtilizationLimit {
  /** `session` | `weekly` | `weeklyModel` | `other`. A string rather than a
      union of four, because `other` exists precisely so a kind nobody has seen
      yet still renders - and a union would make the fifth one a type error at
      the moment it most needs to be a row. */
  window: string;
  /** What the row is called on screen, decided on the server: `Session`,
      `Weekly`, the account's own name for a model, or a humanised `kind`. No
      model name is written down in Hatch. */
  label: string;
  percent: number;
  /** `normal` | `warn` | `danger` - the decision, already made. The client
      paints what it is told; the rule lives in one file on the server. */
  tone: string;
  /** Null on a window with nothing to run down to - the scoped weekly row
      arrives that way. Drawn as unknown, not as full and not as empty. */
  resetsAt: string | null;
  isActive: boolean;
}

/** Extra usage, when the account reports any. Mirrors UtilizationCredits. */
export interface UtilizationCredits {
  isEnabled: boolean;
  monthlyLimit: number | null;
  usedCredits: number;
  currency: string | null;
  spendLimitReached: boolean;
}

/** A reading of the account's Claude headroom. Mirrors UtilizationReading. */
export interface Utilization {
  /** `ok` read within the freshness window; `stale` the account could not be
      reached and this is the last good reading, whose age `readAt` gives;
      `unknown` could not be reached and there has never been one, so `limits`
      is empty and `readAt` is null. */
  state: string;
  readAt: string | null;
  /** In the order the account listed them. Nothing here sorts or filters. */
  limits: UtilizationLimit[];
  /** Null when the account reports no extra usage block at all, which is what
      a modal that says nothing about credits is drawn from. */
  credits: UtilizationCredits | null;
}

// ---- The work log ----

/** What one model cost inside one session. Mirrors WorkLogModelUseDto. */
export interface WorkLogModelUse {
  model: string;
  inputTokens: number;
  outputTokens: number;
  cacheCreationTokens: number;
  cacheReadTokens: number;
  costUsd: number;
}

/** One agent session against one issue. Mirrors WorkLogEntryDto. */
export interface WorkLogEntry {
  id: number;
  /** What `claude --resume` takes. Drawn as a copyable command, not as an id. */
  sessionId: string;
  /** Wall clock either side of the CLI. Deliberately not the same span as
      `durationMs`, which is what the session itself reported. */
  startedAt: string;
  endedAt: string;
  durationMs: number;
  /** Null when the session never said what it did - see `described`. */
  title: string | null;
  summary: string | null;
  /** Whether the session said what it did. On the wire rather than inferred
      from an empty title: "this run never described itself" is a fact about the
      run, and deducing it from an absent string is a client guessing. */
  described: boolean;
  isError: boolean;
  turns: number;
  /** Notional API list price, not money that left an account. Secondary to the
      tokens everywhere it is drawn. */
  costUsd: number;
  inputTokens: number;
  outputTokens: number;
  cacheCreationTokens: number;
  cacheReadTokens: number;
  /** The four, added up on the server so the headline has one definition. */
  totalTokens: number;
  /** The per-model breakdown the four counts are the sum of. Empty on a run
      that ended before the accounting arrived. */
  models: WorkLogModelUse[];
}

/** What a set of sessions cost, added up. Mirrors WorkLogTotalsDto. */
export interface WorkLogTotals {
  sessions: number;
  /** How many ended badly. Their spend is in the totals either way. */
  errors: number;
  inputTokens: number;
  outputTokens: number;
  cacheCreationTokens: number;
  cacheReadTokens: number;
  totalTokens: number;
  costUsd: number;
}

/** An issue's work log. Mirrors WorkLogDto.

    The asymmetry is the point: **entries are the issue's own, totals are the
    subtree's**. An epic showing one session and 1.4M tokens is not a bug, and
    `own` is what lets the page say which number is which. */
export interface WorkLog {
  key: string;
  /** This issue and every descendant, at any depth. */
  totals: WorkLogTotals;
  /** Only the sessions run against this issue. */
  own: WorkLogTotals;
  /** This issue's own sessions, newest first. */
  entries: WorkLogEntry[];
}
