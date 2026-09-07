import { describe, expect, it } from 'vitest';
import { columnDroppableId, place, targetStatusId } from './place';
import type { IssueCard } from '../types';

const INBOX = 1;
const TODO = 2;

const card = (key: string, statusId: number, rank: number): IssueCard => ({
  key,
  projectKey: 'AER',
  type: 'task',
  title: key,
  statusId,
  rank,
  parentKey: null,
  readyAt: null,
  dueAt: null,
  openQuestions: 0,
  claim: null,
});

/* The board as the API hands it over: grouped by column, ranked within it. */
const board = [
  card('AER-1', INBOX, 1024),
  card('AER-2', INBOX, 2048),
  card('AER-3', INBOX, 3072),
  card('AER-9', TODO, 1024),
];

const keysIn = (issues: IssueCard[], statusId: number) =>
  issues.filter((i) => i.statusId === statusId).map((i) => i.key);

describe('targetStatusId', () => {
  it('reads a column dropped onto directly', () => {
    expect(targetStatusId(columnDroppableId(TODO), board)).toBe(TODO);
  });

  it('reads the column of the card under the cursor', () => {
    expect(targetStatusId('AER-9', board)).toBe(TODO);
  });

  it('has nothing to say about no target or an unknown one', () => {
    expect(targetStatusId(null, board)).toBeNull();
    expect(targetStatusId('AER-404', board)).toBeNull();
  });
});

describe('place, within a column', () => {
  it('drops a card below the one it was dragged onto when it came from above', () => {
    const placed = place(board, board, 'AER-1', 'AER-3')!;

    expect(placed.afterKey).toBe('AER-3');
    expect(placed.beforeKey).toBeNull();
    expect(keysIn(placed.issues, INBOX)).toEqual(['AER-2', 'AER-3', 'AER-1']);
  });

  it('drops it above the one it was dragged onto when it came from below', () => {
    const placed = place(board, board, 'AER-3', 'AER-1')!;

    expect(placed.afterKey).toBeNull();
    expect(placed.beforeKey).toBe('AER-1');
    expect(keysIn(placed.issues, INBOX)).toEqual(['AER-3', 'AER-1', 'AER-2']);
  });

  it('names both neighbours when it lands in the middle', () => {
    const placed = place(board, board, 'AER-1', 'AER-2')!;

    expect(placed.afterKey).toBe('AER-2');
    expect(placed.beforeKey).toBe('AER-3');
  });

  it('is nothing at all when a card is dropped where it already was', () => {
    expect(place(board, board, 'AER-1', 'AER-1')).toBeNull();
  });

  it('is nothing at all when there is no drop target', () => {
    expect(place(board, board, 'AER-1', null)).toBeNull();
  });
});

describe('place, across columns', () => {
  it('lands above the card it was dropped on', () => {
    const placed = place(board, board, 'AER-1', 'AER-9')!;

    expect(placed.statusId).toBe(TODO);
    expect(placed.afterKey).toBeNull();
    expect(placed.beforeKey).toBe('AER-9');
    expect(keysIn(placed.issues, TODO)).toEqual(['AER-1', 'AER-9']);
    expect(keysIn(placed.issues, INBOX)).toEqual(['AER-2', 'AER-3']);
  });

  it('lands at the bottom when dropped on the column rather than on a card', () => {
    const placed = place(board, board, 'AER-1', columnDroppableId(TODO))!;

    expect(placed.afterKey).toBe('AER-9');
    expect(placed.beforeKey).toBeNull();
    expect(keysIn(placed.issues, TODO)).toEqual(['AER-9', 'AER-1']);
  });

  /* The move the whole droppable-per-column arrangement exists for: "put this
     in done" when done is empty and has no card to aim at. */
  it('moves into an empty column with no neighbours to name', () => {
    const placed = place(board, board, 'AER-1', columnDroppableId(99))!;

    expect(placed.statusId).toBe(99);
    expect(placed.afterKey).toBeNull();
    expect(placed.beforeKey).toBeNull();
    expect(keysIn(placed.issues, 99)).toEqual(['AER-1']);
  });

  it('moving to the bottom of its own column is still a move', () => {
    const placed = place(board, board, 'AER-1', columnDroppableId(INBOX))!;

    expect(placed.afterKey).toBe('AER-3');
    expect(keysIn(placed.issues, INBOX)).toEqual(['AER-2', 'AER-3', 'AER-1']);
  });
});

/* The reason this is a module with tests rather than a closure in the page.
   With a filter on, the operator drops a card next to a card they can see, and
   the hidden rows in between must not be what decides where it goes. */
describe('place, with the board filtered', () => {
  const visible = [board[0], board[2], board[3]]; // AER-2 filtered out of the inbox

  it('names the visible neighbours, not the hidden ones', () => {
    const placed = place(board, visible, 'AER-1', 'AER-3')!;

    expect(placed.afterKey).toBe('AER-3');
    expect(placed.beforeKey).toBeNull();
  });

  it('leaves the hidden card on the board it paints', () => {
    const placed = place(board, visible, 'AER-1', 'AER-3')!;

    expect(keysIn(placed.issues, INBOX)).toEqual(['AER-2', 'AER-3', 'AER-1']);
  });

  it('puts the moved card next to the visible card it was dropped on', () => {
    const placed = place(board, visible, 'AER-3', 'AER-1')!;

    expect(placed.beforeKey).toBe('AER-1');
    expect(keysIn(placed.issues, INBOX)).toEqual(['AER-3', 'AER-1', 'AER-2']);
  });

  it('still moves a filtered-out card that was dropped onto', () => {
    const placed = place(board, visible, 'AER-1', columnDroppableId(TODO))!;

    expect(placed.statusId).toBe(TODO);
    expect(keysIn(placed.issues, TODO)).toEqual(['AER-9', 'AER-1']);
  });
});

describe('place, when the board has moved underneath', () => {
  it('has nothing to say about a card that is no longer there', () => {
    expect(place(board, board, 'AER-404', 'AER-1')).toBeNull();
    expect(place(board, board, 'AER-1', 'AER-404')).toBeNull();
  });
});
