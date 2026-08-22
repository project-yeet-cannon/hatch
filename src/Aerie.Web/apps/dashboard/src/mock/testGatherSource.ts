import type { GatherItem, GatherListDetail, GatherListSummary, GatherSource } from '../types';

/**
 * Gather's half of the all-X / all-9999 source (see testDataSource.ts): every
 * string is X, every count is 9999, so anything the tile or the overlay draws
 * from its own hardcoded strings stands out immediately. Long names are the
 * point on a wall display — an item that has to wrap or truncate has to do it
 * somewhere, and this is where that shows up.
 *
 * Writes are accepted and change nothing. This source exists to expose
 * hardcoded content, not to model behaviour; the mock source is where adding
 * and checking actually work.
 */

const X = 'XXXX';
const X_LONG = 'XXXXXXXXXXXXXXXX';
const X_LONGEST = 'XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX';
const NUM = 9999;

// Timestamps stay real: the overlay orders and compares them, and an
// unparseable date is a different bug from a hardcoded string.
const NOW = () => new Date().toISOString();

function xItem(listId: string, index: number): GatherItem {
  const checked = index % 3 === 0;
  return {
    id: `${X}-item-${index}`,
    listId,
    name: index === 1 ? X_LONGEST : X_LONG,
    quantity: X,
    note: index === 2 ? X_LONGEST : null,
    isChecked: checked,
    checkedAt: checked ? NOW() : null,
    createdAt: NOW(),
    updatedAt: NOW(),
  };
}

function xList(index: number): GatherListSummary {
  return {
    id: `${X}-list-${index}`,
    name: index === 1 ? X_LONG : X,
    icon: '🐾',
    // An unrecognised colour on purpose: it exercises the palette fallback.
    color: X,
    openCount: NUM,
    checkedCount: NUM,
    createdAt: NOW(),
    updatedAt: NOW(),
  };
}

export class TestGatherSource implements GatherSource {
  getLists(): Promise<GatherListSummary[]> {
    return Promise.resolve([1, 2, 3].map(xList));
  }

  getList(listId: string): Promise<GatherListDetail> {
    const list = xList(1);
    return Promise.resolve({
      list: { ...list, id: listId },
      items: [1, 2, 3, 4, 5, 6].map((i) => xItem(listId, i)),
    });
  }

  addItem(listId: string, _name: string): Promise<GatherItem> {
    return Promise.resolve(xItem(listId, 1));
  }

  setChecked(listId: string, itemId: string, isChecked: boolean): Promise<GatherItem> {
    return Promise.resolve({ ...xItem(listId, 1), id: itemId, isChecked, checkedAt: isChecked ? NOW() : null });
  }

  clearChecked(_listId: string): Promise<number> {
    return Promise.resolve(NUM);
  }
}
