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
  readyAt: string | null;
  dueAt: string | null;
  createdBy: string;
  createdAt: string;
  updatedAt: string;
}

export interface Comment {
  id: number;
  author: string;
  body: string;
  createdAt: string;
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
  | 'commented'
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

/** Null leaves a field alone. An empty `parentKey`, `readyAt` or `dueAt` clears
    it - see IssuePatchRequest in Dtos.cs for why the empty string carries that
    meaning. */
export interface IssuePatchRequest {
  title?: string | null;
  description?: string | null;
  type?: IssueType | null;
  statusId?: number | null;
  parentKey?: string | null;
  readyAt?: string | null;
  dueAt?: string | null;
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
}

// ---- Playbooks ----

/** What the CLI's `--model` takes. Aliases rather than pinned ids, because a
    playbook says "the big one" and should still mean it a year from now. */
export type PlaybookModel = 'haiku' | 'sonnet' | 'opus' | 'fable';

export const PLAYBOOK_MODELS: PlaybookModel[] = ['haiku', 'sonnet', 'opus', 'fable'];

/** What the CLI's `--effort` takes, cheapest first. */
export type PlaybookEffort = 'low' | 'medium' | 'high' | 'xhigh' | 'max';

export const PLAYBOOK_EFFORTS: PlaybookEffort[] = ['low', 'medium', 'high', 'xhigh', 'max'];

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
