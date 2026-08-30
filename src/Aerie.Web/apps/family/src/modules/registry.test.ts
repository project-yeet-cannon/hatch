import { describe, expect, it } from 'vitest';
import { modules, visibleModules } from './registry';
import type { Session } from '../lib/session';

const ada: Session = { personId: 'ada', personName: 'Ada' };
const unclaimed: Session = { personId: null, personName: null };

describe('visibleModules', () => {
  it('shows the household apps to anyone, claimed device or not', () => {
    const open = modules.filter((m) => !m.requiresPerson).map((m) => m.id);

    expect(visibleModules(null).map((m) => m.id)).toEqual(open);
    expect(visibleModules(unclaimed).map((m) => m.id)).toEqual(open);
  });

  it('shows an owner-requiring app only to a session with a person', () => {
    // Quill is the first, and the reason this function exists: a device nobody
    // has claimed should not be able to tell that it is there.
    expect(visibleModules(null).map((m) => m.id)).not.toContain('quill');
    expect(visibleModules(ada).map((m) => m.id)).toContain('quill');
  });

  it('shows everything to a claimed device', () => {
    expect(visibleModules(ada)).toHaveLength(modules.length);
  });

  it('leaves the registry itself alone', () => {
    const before = modules.map((m) => m.id);

    visibleModules(null);

    expect(modules.map((m) => m.id)).toEqual(before);
  });
});
