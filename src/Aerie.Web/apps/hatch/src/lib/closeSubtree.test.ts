import { describe, expect, it } from 'vitest';
import { closeOffer, closeSubtree } from './closeSubtree';
import type { Board, IssueCard, Status } from '../types';

const TODO = 1;
const DOING = 2;
const DONE = 3;
const DROPPED = 4;

const statuses: Status[] = [
  { id: TODO, name: 'To Do', sortOrder: 1, isTerminal: false, color: '#888888' },
  { id: DOING, name: 'In Progress', sortOrder: 2, isTerminal: false, color: '#888888' },
  { id: DONE, name: 'Done', sortOrder: 3, isTerminal: true, color: '#888888' },
  { id: DROPPED, name: "Won't Do", sortOrder: 4, isTerminal: true, color: '#888888' },
];

const card = (key: string, statusId: number, parentKey: string | null, rank = 1024): IssueCard => ({
  key,
  projectKey: 'AER',
  type: 'task',
  title: `${key} title`,
  statusId,
  rank,
  parentKey,
  readyAt: null,
  dueAt: null,
  openQuestions: 0,
  assignee: null,
  claim: null,
});

const keys = (cards: IssueCard[]) => cards.map((c) => c.key);

/* An epic (AER-1) with two stories under it (AER-2, AER-4) and a task under
   each (AER-3, AER-5), plus an unrelated leaf - as the API hands the board
   over, grouped by column and ranked within it. Board order and tree order are
   deliberately different here: that is the distinction worth testing. */
const cards: IssueCard[] = [
  card('AER-2', TODO, 'AER-1', 1024),
  card('AER-3', TODO, 'AER-2', 2048),
  card('AER-5', TODO, 'AER-4', 3072),
  card('AER-9', TODO, null, 4096),
  card('AER-1', DOING, null, 1024),
  card('AER-4', DOING, 'AER-1', 2048),
];

const board: Board = { statuses, issues: cards };

describe('closeSubtree', () => {
  it('finds children and grandchildren at any depth, and never the issue itself', () => {
    expect(keys(closeSubtree(cards, 'AER-1', statuses))).toEqual(['AER-2', 'AER-3', 'AER-5', 'AER-4']);
  });

  it('finds nothing under a leaf', () => {
    expect(closeSubtree(cards, 'AER-9', statuses)).toEqual([]);
  });

  it('leaves a descendant that is already in a terminal column where it is', () => {
    const closed = cards.map((c) => (c.key === 'AER-3' ? { ...c, statusId: DONE } : c));

    expect(keys(closeSubtree(closed, 'AER-1', statuses))).toEqual(['AER-2', 'AER-5', 'AER-4']);
  });

  /* A story that shipped can still have a task nobody closed. The terminal
     filter is applied to the cards, not to the walk, so the walk reaches
     through it. */
  it('reaches through a terminal descendant to the open work under it', () => {
    const closed = cards.map((c) => (c.key === 'AER-2' ? { ...c, statusId: DROPPED } : c));

    expect(keys(closeSubtree(closed, 'AER-1', statuses))).toEqual(['AER-3', 'AER-5', 'AER-4']);
  });

  it('has nothing to move when every descendant is already closed', () => {
    const closed = cards.map((c) => (c.parentKey === null ? c : { ...c, statusId: DONE }));

    expect(closeSubtree(closed, 'AER-1', statuses)).toEqual([]);
  });

  /* Board order, not walk order: AER-5 is a grandchild and AER-4 its parent,
     and the pair comes back the way the board handed them over. That is the
     order the dialog lists and the order the bulk edit names. */
  it('comes back in the order the cards arrived in, not the order they were walked', () => {
    const shuffled = ['AER-5', 'AER-1', 'AER-4', 'AER-9', 'AER-3', 'AER-2'].map(
      (key) => cards.find((c) => c.key === key)!,
    );

    expect(keys(closeSubtree(shuffled, 'AER-1', statuses))).toEqual(['AER-5', 'AER-4', 'AER-3', 'AER-2']);
  });

  /* Parenting refuses to close a loop; a restored backup does not. A wrong
     answer beats a browser that stops responding. */
  it('terminates on a parent cycle, and returns each card once', () => {
    const looped = [card('AER-1', DOING, 'AER-3'), card('AER-2', TODO, 'AER-1'), card('AER-3', TODO, 'AER-2')];

    expect(keys(closeSubtree(looped, 'AER-1', statuses))).toEqual(['AER-2', 'AER-3']);
  });

  /* The fold is a view, not a fact: work that cannot start until August is
     still work under this issue, and closing the parent closes it too. */
  it('includes a descendant whose ready date has not arrived', () => {
    const later = cards.map((c) => (c.key === 'AER-5' ? { ...c, readyAt: '2099-08-15' } : c));

    expect(keys(closeSubtree(later, 'AER-1', statuses))).toContain('AER-5');
  });
});

describe('closeOffer', () => {
  it('offers the terminal column it was pointed at, and the cards under the issue', () => {
    const offer = closeOffer(board, 'AER-1', DOING, DONE)!;

    expect(offer.key).toBe('AER-1');
    expect(offer.column.name).toBe('Done');
    expect(keys(offer.cards)).toEqual(['AER-2', 'AER-3', 'AER-5', 'AER-4']);
  });

  /* Not a canonical closed column: a subtree abandoned into Won't Do should
     read as abandoned. */
  it('offers whichever terminal column the issue landed in', () => {
    expect(closeOffer(board, 'AER-1', DOING, DROPPED)!.column.name).toBe("Won't Do");
  });

  it('asks nothing about a move into a column that is not terminal', () => {
    expect(closeOffer(board, 'AER-1', TODO, DOING)).toBeNull();
  });

  it('asks nothing about a reorder inside the column the card was already in', () => {
    const closed: Board = { statuses, issues: cards.map((c) => (c.key === 'AER-1' ? { ...c, statusId: DONE } : c)) };

    expect(closeOffer(closed, 'AER-1', DONE, DONE)).toBeNull();
  });

  it('asks nothing about a column that is not on the board', () => {
    expect(closeOffer(board, 'AER-1', DOING, 404)).toBeNull();
  });

  it('asks nothing when the issue has nothing open under it', () => {
    expect(closeOffer(board, 'AER-9', TODO, DONE)).toBeNull();
  });
});
