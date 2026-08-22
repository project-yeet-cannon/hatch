import type { GatherItem, GatherListDetail, GatherListSummary, GatherSource } from '../types';

/**
 * Gather with no API behind it: synthetic lists that actually mutate, so
 * `?source=mock` is a usable surface for working on the overlay rather than a
 * screenshot of one. Adding, checking and sweeping all behave the way the
 * module does — including the upsert on a re-add — because an overlay that
 * only looks right against data that never changes isn't verified at all.
 *
 * The server semantics reproduced here are documented in
 * src/Aerie.Api/Modules/Gather/GatherService.cs; if the two ever disagree, that
 * one is right.
 */

const LATENCY_MS = 120;

const HOUR_MS = 60 * 60 * 1000;

interface MockItem extends GatherItem {
  nameNormalized: string;
}

interface MockList extends Omit<GatherListSummary, 'openCount' | 'checkedCount'> {
  items: MockItem[];
}

let nextId = 1;
function id(): string {
  // Shaped like a Guid so nothing downstream can quietly depend on the format.
  return `00000000-0000-4000-8000-${String(nextId++).padStart(12, '0')}`;
}

/** Case and whitespace only — matches GatherItem.Normalize, which is not a stemmer. */
function normalize(name: string): string {
  return name.trim().replace(/\s+/g, ' ').toLowerCase();
}

function makeItem(name: string, quantity: string | null, ageHours: number, checkedHoursAgo: number | null): MockItem {
  const created = new Date(Date.now() - ageHours * HOUR_MS).toISOString();
  const checkedAt = checkedHoursAgo === null ? null : new Date(Date.now() - checkedHoursAgo * HOUR_MS).toISOString();
  return {
    id: id(),
    listId: '',
    name,
    nameNormalized: normalize(name),
    quantity,
    note: null,
    isChecked: checkedAt !== null,
    checkedAt,
    createdAt: created,
    updatedAt: checkedAt ?? created,
  };
}

function makeList(name: string, icon: string, color: string, items: MockItem[]): MockList {
  const listId = id();
  for (const item of items) item.listId = listId;
  const created = new Date(Date.now() - 30 * 24 * HOUR_MS).toISOString();
  return {
    id: listId,
    name,
    icon,
    color,
    createdAt: created,
    updatedAt: new Date(Date.now() - HOUR_MS).toISOString(),
    items,
  };
}

// Deliberately uneven: one list mid-shop with things already in the cart, one
// short, one empty. The empty one is the case the tile and the list screen are
// both easiest to get wrong.
const LISTS: MockList[] = [
  makeList('Grocery', '🛒', 'moss', [
    makeItem('2% milk', 'x2', 26, null),
    makeItem('Sourdough', null, 25, null),
    makeItem('Baby spinach', '2 bags', 22, null),
    makeItem('Sharp cheddar', null, 20, null),
    makeItem('Coffee beans', '12 oz', 18, null),
    makeItem('Roma tomatoes', '6', 9, null),
    makeItem('Olive oil', null, 4, null),
    makeItem('Eggs', 'a dozen', 30, 1.5),
    makeItem('Bananas', null, 28, 2),
  ]),
  makeList('Hardware', '🔧', 'clay', [
    makeItem('Furnace filter', '20x25x1', 50, null),
    makeItem('Wood screws', '1 1/4"', 47, null),
  ]),
  makeList('Pharmacy', '💊', 'sky', []),
];

function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY_MS));
}

function find(listId: string): MockList {
  const list = LISTS.find((l) => l.id === listId);
  if (!list) throw new Error(`No such list: ${listId}`);
  return list;
}

function toSummary(list: MockList): GatherListSummary {
  const { items, ...rest } = list;
  return {
    ...rest,
    openCount: items.filter((i) => !i.isChecked).length,
    checkedCount: items.filter((i) => i.isChecked).length,
  };
}

function toItem(item: MockItem): GatherItem {
  const { nameNormalized: _nameNormalized, ...rest } = item;
  return { ...rest };
}

/** Unchecked first by age, then checked most-recently-first — GatherService.InDisplayOrder. */
function inDisplayOrder(items: MockItem[]): MockItem[] {
  const open = items.filter((i) => !i.isChecked).sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  const done = items.filter((i) => i.isChecked).sort((a, b) => (b.checkedAt ?? '').localeCompare(a.checkedAt ?? ''));
  return [...open, ...done];
}

function touch(list: MockList, item: MockItem | null): void {
  const now = new Date().toISOString();
  list.updatedAt = now;
  if (item) item.updatedAt = now;
}

export class MockGatherSource implements GatherSource {
  getLists(): Promise<GatherListSummary[]> {
    return delay([...LISTS].sort((a, b) => a.name.localeCompare(b.name)).map(toSummary));
  }

  getList(listId: string): Promise<GatherListDetail> {
    const list = find(listId);
    return delay({ list: toSummary(list), items: inDisplayOrder(list.items).map(toItem) });
  }

  addItem(listId: string, name: string): Promise<GatherItem> {
    const list = find(listId);
    const normalized = normalize(name);
    const existing = list.items.find((i) => i.nameNormalized === normalized);

    if (existing) {
      // A re-add keeps the spelling already on the list and brings the item
      // back open — it is a re-add, not a rename and not a no-op.
      existing.isChecked = false;
      existing.checkedAt = null;
      touch(list, existing);
      return delay(toItem(existing));
    }

    const item = makeItem(name.trim(), null, 0, null);
    item.listId = listId;
    list.items.push(item);
    touch(list, item);
    return delay(toItem(item));
  }

  setChecked(listId: string, itemId: string, isChecked: boolean): Promise<GatherItem> {
    const list = find(listId);
    const item = list.items.find((i) => i.id === itemId);
    if (!item) throw new Error(`No such item: ${itemId}`);

    item.isChecked = isChecked;
    item.checkedAt = isChecked ? new Date().toISOString() : null;
    touch(list, item);
    return delay(toItem(item));
  }

  clearChecked(listId: string): Promise<number> {
    const list = find(listId);
    const before = list.items.length;
    list.items = list.items.filter((i) => !i.isChecked);
    touch(list, null);
    return delay(before - list.items.length);
  }
}
