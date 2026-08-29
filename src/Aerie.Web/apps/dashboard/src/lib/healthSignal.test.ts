import { describe, expect, it, vi } from 'vitest';
import { createHealthSignal, levelForCleanPolls } from './healthSignal';

describe('levelForCleanPolls', () => {
  it('holds full strength through the first three clean polls', () => {
    expect(levelForCleanPolls(0)).toBe(100);
    expect(levelForCleanPolls(2)).toBe(100);
  });

  it('steps down at each boundary', () => {
    expect(levelForCleanPolls(3)).toBe(60);
    expect(levelForCleanPolls(5)).toBe(60);
    expect(levelForCleanPolls(6)).toBe(30);
    expect(levelForCleanPolls(8)).toBe(30);
  });

  it('is gone at nine', () => {
    expect(levelForCleanPolls(9)).toBe(0);
    expect(levelForCleanPolls(400)).toBe(0);
  });
});

describe('createHealthSignal', () => {
  it('starts invisible', () => {
    const signal = createHealthSignal(() => {});
    expect(signal.snapshot()).toEqual({ level: 0, cleanPolls: 0 });
  });

  it('stays invisible while nothing has gone wrong, however long it runs', () => {
    const onChange = vi.fn();
    const signal = createHealthSignal(onChange);
    for (let i = 0; i < 50; i++) signal.notePollSuccess();
    expect(signal.snapshot()).toEqual({ level: 0, cleanPolls: 0 });
    expect(onChange).not.toHaveBeenCalled();
  });

  it('goes to full strength on an error', () => {
    const signal = createHealthSignal(() => {});
    signal.noteError();
    expect(signal.snapshot().level).toBe(100);
  });

  it('fades over consecutive clean polls and then goes away', () => {
    const signal = createHealthSignal(() => {});
    signal.noteError();

    const levels: number[] = [];
    for (let i = 0; i < 9; i++) {
      signal.notePollSuccess();
      levels.push(signal.snapshot().level);
    }

    expect(levels).toEqual([100, 100, 60, 60, 60, 30, 30, 30, 0]);
  });

  it('restarts the decay when a new error lands mid-fade', () => {
    const signal = createHealthSignal(() => {});
    signal.noteError();
    signal.notePollSuccess();
    signal.notePollSuccess();
    signal.notePollSuccess();
    expect(signal.snapshot()).toEqual({ level: 60, cleanPolls: 3 });

    signal.noteError();
    expect(signal.snapshot()).toEqual({ level: 100, cleanPolls: 0 });
  });

  it('notifies on a repeat error at full strength, because the modal changed', () => {
    const onChange = vi.fn();
    const signal = createHealthSignal(onChange);
    signal.noteError();
    onChange.mockClear();

    signal.noteError();
    expect(onChange).toHaveBeenCalledWith({ level: 100, cleanPolls: 0 });
  });

  it('does not notify for a poll that changes nothing', () => {
    const onChange = vi.fn();
    const signal = createHealthSignal(onChange);
    signal.noteError();
    onChange.mockClear();

    signal.notePollSuccess();
    expect(onChange).not.toHaveBeenCalled();

    signal.notePollSuccess();
    signal.notePollSuccess();
    expect(onChange).toHaveBeenCalledExactlyOnceWith({ level: 60, cleanPolls: 3 });
  });

  it('does not creep back up after recovering, until something else breaks', () => {
    const signal = createHealthSignal(() => {});
    signal.noteError();
    for (let i = 0; i < 9; i++) signal.notePollSuccess();
    expect(signal.snapshot().level).toBe(0);

    // The wall is healthy again. Another hour of good polls must not restart
    // the ladder from a stale count.
    for (let i = 0; i < 60; i++) signal.notePollSuccess();
    expect(signal.snapshot().level).toBe(0);

    signal.noteError();
    expect(signal.snapshot()).toEqual({ level: 100, cleanPolls: 0 });
  });
});
