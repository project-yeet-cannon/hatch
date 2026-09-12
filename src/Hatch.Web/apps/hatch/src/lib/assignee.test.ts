import { describe, expect, it } from 'vitest';
import { UNASSIGNED, assigneeHint, assigneeToken, compareAssignees, sameAssignee } from './assignee';
import type { Assignee } from '../types';

const person = (name: string, id = `p-${name}`): Assignee => ({ kind: 'person', id, name });
const key = (name: string, id = `k-${name}`): Assignee => ({ kind: 'key', id, name });

describe('assigneeToken', () => {
  it('names the kind as well as the id', () => {
    expect(assigneeToken(person('Ada', '7f3c'))).toBe('person:7f3c');
    expect(assigneeToken(key('Claude', '7f3c'))).toBe('key:7f3c');
  });

  it('cannot collide with the unassigned sentinel', () => {
    // Which is the whole reason the sentinel can be a bare string in the same
    // field a token goes in: every token carries a colon and a guid.
    expect(assigneeToken(person('Ada'))).not.toBe(UNASSIGNED);
  });
});

describe('sameAssignee', () => {
  it('ignores the name, because a rename is not a reassignment', () => {
    expect(sameAssignee(person('Ada', '7f3c'), person('Ada Lovelace', '7f3c'))).toBe(true);
  });

  it('tells two kinds apart even where the ids match', () => {
    expect(sameAssignee(person('Ada', '7f3c'), key('Claude', '7f3c'))).toBe(false);
  });

  it('reads nobody as nobody, and nobody as different from somebody', () => {
    expect(sameAssignee(null, null)).toBe(true);
    expect(sameAssignee(person('Ada'), null)).toBe(false);
    expect(sameAssignee(null, person('Ada'))).toBe(false);
  });
});

describe('compareAssignees', () => {
  it('puts every person before every key, and sorts each A→Z', () => {
    const rows = [key('Claude'), person('Zoe'), key('Ansible'), person('Ada')];

    expect([...rows].sort(compareAssignees).map((a) => a.name)).toEqual(['Ada', 'Zoe', 'Ansible', 'Claude']);
  });
});

describe('assigneeHint', () => {
  it("says the loop leaves a person's ticket alone", () => {
    expect(assigneeHint(person('Ada'))).toContain('leaves it alone');
  });

  it("says a key's ticket is still picked up, because that is the surprising half", () => {
    expect(assigneeHint(key('Claude'))).toContain('still picked up');
  });

  it('says an unowned ticket is fair game', () => {
    expect(assigneeHint(null)).toContain('may pick it up');
  });
});
