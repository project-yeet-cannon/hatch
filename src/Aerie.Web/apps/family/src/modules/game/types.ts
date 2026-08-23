/** The game module's half of the API contract - see Modules/Game/Dtos.cs. */

export interface WorldSummary {
  id: string;
  name: string;
  icon: string | null;
  versionCount: number;
  lastPrompt: string | null;
  createdAt: string;
  updatedAt: string;
}

/** Why a version exists. Serialised as a string by the API's enum converter. */
export type VersionKind = 'Seed' | 'Turn' | 'Repair' | 'Revert';

export interface Version {
  id: string;
  ordinal: number;
  kind: VersionKind;
  prompt: string | null;
  summary: string | null;
  extra: string | null;
  model: string | null;
  createdAt: string;
  durationMs: number;
  inputTokens: number;
  outputTokens: number;
  cachedInputTokens: number;
  isBroken: boolean;
}

export interface GameRecord {
  id: string;
  label: string;
  value: number;
  unit: string | null;
  lowerIsBetter: boolean;
}

/** A world and the code it currently runs - what the play screen loads and what every turn returns. */
export interface World {
  world: WorldSummary;
  current: Version | null;
  code: string;
  records: GameRecord[];
}

/**
 * Which model writes the turn. The names are about the wait, not the model:
 * that is the only difference the person typing can feel, and it keeps model
 * ids server-side (see GameAuthor.ModelIdFor).
 */
export type ModelChoice = 'Quick' | 'Careful';

export interface Capability {
  canAuthor: boolean;
  reason: string | null;
}

/** What the frame reports when the code it was handed threw. */
export interface Breakage {
  versionId: string;
  phase: string;
  message: string;
  stack: string | null;
}
