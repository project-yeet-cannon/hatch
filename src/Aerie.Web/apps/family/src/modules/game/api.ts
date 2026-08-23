import { asJson, createClient } from '../../lib/http';
import type { Breakage, Capability, GameRecord, ModelChoice, Version, World, WorldSummary } from './types';

/**
 * The game module's routes, over the shell's own client (src/lib/http.ts).
 *
 * One thing here is unlike every other module: `takeTurn` is a request that
 * routinely runs for half a minute, because a model is writing a program on the
 * other end. It gets no timeout of its own - the caller owns the waiting, and
 * an abort signal is how a screen that goes away stops caring.
 */

const { fetchJson } = createClient('/api/game');

export const getCapability = (signal?: AbortSignal) => fetchJson<Capability>('/capability', { signal });

// ---- Worlds ----

export const getWorlds = (signal?: AbortSignal) => fetchJson<WorldSummary[]>('/worlds', { signal });

/** The last-played world, created on the spot if the house has never played. */
export const getCurrentWorld = (signal?: AbortSignal) => fetchJson<World>('/worlds/current', { signal });

export const getWorld = (id: string, signal?: AbortSignal) => fetchJson<World>(`/worlds/${id}`, { signal });

export const createWorld = (name: string, icon: string) =>
  fetchJson<World>('/worlds', { method: 'POST', ...asJson({ name, icon }) });

export const renameWorld = (id: string, name: string, icon: string | null) =>
  fetchJson<WorldSummary>(`/worlds/${id}`, { method: 'PUT', ...asJson({ name, icon }) });

/** Takes the world's whole history with it. */
export const deleteWorld = (id: string) => fetchJson<void>(`/worlds/${id}`, { method: 'DELETE' });

// ---- Turns ----

/** The slow one: a model writes a new version of the game and it comes back live. */
export const takeTurn = (id: string, prompt: string, model: ModelChoice, signal?: AbortSignal) =>
  fetchJson<World>(`/worlds/${id}/turns`, { method: 'POST', signal, ...asJson({ prompt, model }) });

/** Reports a crash and asks for a fix in the same call. */
export const repair = (id: string, breakage: Breakage, signal?: AbortSignal) =>
  fetchJson<World>(`/worlds/${id}/repair`, { method: 'POST', signal, ...asJson(breakage) });

/** Reports a crash without asking for a fix - the client has given up repairing. */
export const reportBreakage = (id: string, breakage: Breakage) =>
  fetchJson<void>(`/worlds/${id}/breakage`, { method: 'POST', ...asJson(breakage) });

export const undo = (id: string) => fetchJson<World>(`/worlds/${id}/undo`, { method: 'POST' });

export const revert = (id: string, versionId: string) =>
  fetchJson<World>(`/worlds/${id}/revert`, { method: 'POST', ...asJson({ versionId }) });

export const getVersions = (id: string, signal?: AbortSignal) =>
  fetchJson<Version[]>(`/worlds/${id}/versions`, { signal });

// ---- Records ----

export const saveRecords = (id: string, records: GameRecord[]) =>
  fetchJson<void>(`/worlds/${id}/records`, { method: 'PUT', ...asJson({ records }) });
