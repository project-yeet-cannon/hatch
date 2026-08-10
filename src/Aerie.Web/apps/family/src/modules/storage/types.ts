/*
  The wire shapes, mirroring src/Aerie.Api/Modules/Storage/Dtos.cs. Hand-written
  rather than generated, like every other Aerie SPA's types.ts.

  Codes travel in both forms: `code` is what URLs and lookups use, `displayCode`
  is what a label prints and what a screen should show. Nothing here reformats a
  code client-side - the server owns that, so the app and the label can never
  disagree about what's written on the box.
*/

export interface Location {
  id: string;
  name: string;
  description: string | null;
  crateCount: number;
  createdAt: string;
}

export interface LocationWriteRequest {
  name: string;
  description: string | null;
}

export interface Crate {
  id: string;
  code: string;
  displayCode: string;
  label: string | null;
  locationId: string | null;
  locationName: string | null;
  notes: string | null;
  itemCount: number;
  createdAt: string;
  updatedAt: string;
}

/** A crate and everything in it, in one response - the scan destination draws from this alone. */
export interface CrateDetail {
  crate: Crate;
  items: Item[];
}

export interface CrateWriteRequest {
  label: string | null;
  locationId: string | null;
  notes: string | null;
}

export interface Item {
  id: string;
  crateId: string;
  name: string;
  quantity: number;
  notes: string | null;
  createdAt: string;
  updatedAt: string;
}

/** One row of the flat "where is the drill" index - carries the full answer to where it is. */
export interface ItemIndexRow {
  id: string;
  name: string;
  quantity: number;
  notes: string | null;
  crateId: string;
  crateCode: string;
  crateDisplayCode: string;
  crateLabel: string | null;
  locationId: string | null;
  locationName: string | null;
}

export interface ItemWriteRequest {
  crateId: string;
  name: string;
  quantity: number;
  notes: string | null;
}
