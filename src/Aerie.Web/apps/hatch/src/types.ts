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

export interface ProjectPatchRequest {
  name: string;
}

export interface StatusCreateRequest {
  name: string;
  sortOrder?: number | null;
  isTerminal?: boolean | null;
}

export interface StatusPatchRequest {
  name?: string | null;
  sortOrder?: number | null;
  isTerminal?: boolean | null;
}

export interface IssueCreateRequest {
  projectId: number;
  type: IssueType;
  title: string;
  description?: string | null;
  parentKey?: string | null;
}

/** Null leaves a field alone. An empty `parentKey` clears the parent - see
    IssuePatchRequest in Dtos.cs for why the empty string carries that meaning. */
export interface IssuePatchRequest {
  title?: string | null;
  description?: string | null;
  type?: IssueType | null;
  statusId?: number | null;
  parentKey?: string | null;
}

export interface IssueMoveRequest {
  statusId: number;
  afterKey?: string | null;
  beforeKey?: string | null;
}

export interface CommentCreateRequest {
  body: string;
}
