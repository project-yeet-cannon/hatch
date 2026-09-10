import { describe, expect, it } from 'vitest';
import { dismiss, raise } from './confirmations';
import type { Confirmation } from './confirmations';

const filed = (id: number, key: string): Confirmation => ({ id, issueKey: key, title: `title ${key}` });

describe('raise', () => {
  it('takes the key and the title off the issue the server returned', () => {
    expect(raise([], { key: 'AER-12', title: 'Drain retries' }, 1)).toEqual([
      { id: 1, issueKey: 'AER-12', title: 'Drain retries' },
    ]);
  });

  it('puts the newest on the front and leaves what was there', () => {
    const stack = raise([filed(1, 'AER-12')], { key: 'AER-13', title: 'title AER-13' }, 2);

    expect(stack.map((c) => c.issueKey)).toEqual(['AER-13', 'AER-12']);
  });

  /* Two filings are two chicklets. Nothing replaces anything, so a planning
     session that files six can still see all six. */
  it('never replaces a chicklet already on screen', () => {
    let stack: Confirmation[] = [];
    for (const key of ['AER-12', 'AER-13', 'AER-14']) {
      stack = raise(stack, { key, title: `title ${key}` }, stack.length + 1);
    }

    expect(stack).toHaveLength(3);
    expect(new Set(stack.map((c) => c.id)).size).toBe(3);
  });

  /* The id is what keeps them apart, not the key: the same issue confirmed
     twice is two rows that can be closed one at a time. */
  it('keeps two filings of the same key apart', () => {
    const stack = raise(raise([], { key: 'AER-12', title: 'Drain retries' }, 1), { key: 'AER-12', title: 'Drain retries' }, 2);

    expect(stack.map((c) => c.id)).toEqual([2, 1]);
  });

  it('does not touch the stack it was given', () => {
    const before = [filed(1, 'AER-12')];
    raise(before, { key: 'AER-13', title: 'title AER-13' }, 2);

    expect(before).toHaveLength(1);
  });
});

describe('dismiss', () => {
  it('takes exactly one off and leaves the rest in order', () => {
    const stack = [filed(3, 'AER-14'), filed(2, 'AER-13'), filed(1, 'AER-12')];

    expect(dismiss(stack, 2).map((c) => c.issueKey)).toEqual(['AER-14', 'AER-12']);
  });

  it('is a no-op for an id that is not in the stack', () => {
    const stack = [filed(2, 'AER-13'), filed(1, 'AER-12')];

    expect(dismiss(stack, 99)).toEqual(stack);
  });

  it('empties a stack of one', () => {
    expect(dismiss([filed(1, 'AER-12')], 1)).toEqual([]);
  });

  it('does not touch the stack it was given', () => {
    const before = [filed(1, 'AER-12')];
    dismiss(before, 1);

    expect(before).toHaveLength(1);
  });
});

/* Criterion 4.3 and 4.4 - a reload starts the corner empty, and a chicklet
   does not turn up in a second tab - hold because the stack is never written
   anywhere a second document could read it. Asserted rather than assumed, so
   a later "remember these across a reload" has to argue with a test. */
describe('the stack lives only in the page', () => {
  it('reads and writes no browser storage', () => {
    const touched: string[] = [];
    const trap = new Proxy(
      {},
      {
        get: (_t, prop) => {
          touched.push(String(prop));
          return undefined;
        },
        set: (_t, prop) => {
          touched.push(String(prop));
          return true;
        },
      },
    );

    const globals = globalThis as unknown as Record<string, unknown>;
    globals.localStorage = trap;
    globals.sessionStorage = trap;
    try {
      dismiss(raise(raise([], { key: 'AER-12', title: 'one' }, 1), { key: 'AER-13', title: 'two' }, 2), 1);
    } finally {
      delete globals.localStorage;
      delete globals.sessionStorage;
    }

    expect(touched).toEqual([]);
  });
});
