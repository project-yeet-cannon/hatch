/*
  The wire shapes, mirroring src/Aerie.Api/Modules/Gather/Dtos.cs. Hand-written
  rather than generated, like every other Aerie SPA's types.ts.

  NameNormalized is deliberately absent: it is the server's business, and the
  thing that makes a re-add an upsert rather than a second "milk" row. Nothing
  here should ever try to reproduce it.
*/

export interface ListSummary {
  id: string;
  name: string;
  icon: string | null;
  /** A palette name, not a hex - see palette.ts. */
  color: string | null;
  openCount: number;
  checkedCount: number;
  createdAt: string;
  updatedAt: string;
}

/** A list and everything on it, in one response - what opening a list draws and what the poll re-fetches. */
export interface ListDetail {
  list: ListSummary;
  items: Item[];
}

export interface Item {
  id: string;
  listId: string;
  name: string;
  /** Free text on purpose: "a dozen", "2 lbs" and "x2" are all real answers. */
  quantity: string | null;
  note: string | null;
  isChecked: boolean;
  checkedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ListWriteRequest {
  name: string;
  icon: string | null;
  color: string | null;
}

/**
 * Adding an item, which the server treats as an upsert on the normalized name.
 * Separate from ItemWriteRequest because the two mean different things: a
 * re-add merges (a bare "milk" can't erase someone else's "2 gal"), an edit
 * overwrites, nulls included.
 */
export interface ItemAddRequest {
  name: string;
  quantity: string | null;
  note: string | null;
}

export interface ItemWriteRequest {
  name: string;
  quantity: string | null;
  note: string | null;
}

export interface ClearCheckedResult {
  deleted: number;
}
