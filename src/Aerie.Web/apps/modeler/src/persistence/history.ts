// Undo/redo as a stack of full document snapshots rather than true patches.
// The architecture note calls for an "in-memory patch stack"; snapshots are
// the simplest correct implementation of that and are cheap at the document
// sizes step 1 through step 4 produce (a handful of sketches/walls). If a
// project ever gets big enough for this to matter, swap the snapshot in
// `push` for a computed diff without touching any call site below.
const MAX_HISTORY = 100;

export interface HistoryState<T> {
  past: T[];
  present: T;
  future: T[];
}

export function createHistory<T>(present: T): HistoryState<T> {
  return { past: [], present, future: [] };
}

export function pushHistory<T>(history: HistoryState<T>, next: T): HistoryState<T> {
  const past = [...history.past, history.present].slice(-MAX_HISTORY);
  return { past, present: next, future: [] };
}

export function undo<T>(history: HistoryState<T>): HistoryState<T> {
  if (history.past.length === 0) return history;
  const previous = history.past[history.past.length - 1];
  return {
    past: history.past.slice(0, -1),
    present: previous,
    future: [history.present, ...history.future],
  };
}

export function redo<T>(history: HistoryState<T>): HistoryState<T> {
  if (history.future.length === 0) return history;
  const [next, ...rest] = history.future;
  return {
    past: [...history.past, history.present],
    present: next,
    future: rest,
  };
}

export function canUndo<T>(history: HistoryState<T>): boolean {
  return history.past.length > 0;
}

export function canRedo<T>(history: HistoryState<T>): boolean {
  return history.future.length > 0;
}
