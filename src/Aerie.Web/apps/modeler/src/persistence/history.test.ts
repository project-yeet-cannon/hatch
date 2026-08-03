import { describe, expect, it } from 'vitest';
import { canRedo, canUndo, createHistory, pushHistory, redo, undo } from './history';

describe('history', () => {
  it('starts with nothing to undo or redo', () => {
    const h = createHistory('a');
    expect(canUndo(h)).toBe(false);
    expect(canRedo(h)).toBe(false);
    expect(h.present).toBe('a');
  });

  it('undo moves present back and populates future', () => {
    let h = createHistory('a');
    h = pushHistory(h, 'b');
    h = pushHistory(h, 'c');

    h = undo(h);
    expect(h.present).toBe('b');
    expect(canUndo(h)).toBe(true);
    expect(canRedo(h)).toBe(true);

    h = undo(h);
    expect(h.present).toBe('a');
    expect(canUndo(h)).toBe(false);
  });

  it('undo on an empty past is a no-op', () => {
    const h = createHistory('a');
    expect(undo(h)).toBe(h);
  });

  it('redo replays what undo took back', () => {
    let h = createHistory('a');
    h = pushHistory(h, 'b');
    h = undo(h);
    h = redo(h);
    expect(h.present).toBe('b');
    expect(canRedo(h)).toBe(false);
  });

  it('redo on an empty future is a no-op', () => {
    const h = createHistory('a');
    expect(redo(h)).toBe(h);
  });

  it('pushing a new state after undo discards the redo branch', () => {
    let h = createHistory('a');
    h = pushHistory(h, 'b');
    h = undo(h);
    h = pushHistory(h, 'c');
    expect(h.present).toBe('c');
    expect(canRedo(h)).toBe(false);
  });

  it('caps history length so it cannot grow unbounded', () => {
    let h = createHistory(0);
    for (let i = 1; i <= 150; i++) {
      h = pushHistory(h, i);
    }
    expect(h.past.length).toBeLessThanOrEqual(100);
    expect(h.present).toBe(150);
  });
});
