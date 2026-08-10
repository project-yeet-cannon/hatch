import { useCallback, useState } from 'react';

/*
  The geometry of a label sheet, kept out of the component because it's
  arithmetic rather than markup.

  Everything here is in millimetres, including on screen: CSS `mm` is a real
  physical unit at print time, so one set of numbers describes the preview and
  the paper. Nothing assumes a label product - the sheet is plain paper, cut
  lines and tape - and nothing assumes Letter beyond it being the default.
*/

export interface PageSize {
  id: string;
  label: string;
  widthMm: number;
  heightMm: number;
}

export const PAGE_SIZES: PageSize[] = [
  { id: 'letter', label: 'Letter', widthMm: 215.9, heightMm: 279.4 },
  { id: 'a4', label: 'A4', widthMm: 210, heightMm: 297 },
];

/** Printer-safe on both sizes, and wide enough that a cut line isn't at the edge. */
export const MARGIN_MM = 10;

/** Space between a cell's dashed cut line and anything inside it. */
const PAD_MM = 3;

export interface LabelLayout {
  pageSize: string;
  columns: number;
  rows: number;
}

/**
 * Two by four on Letter: a ~41mm QR, which the stock iOS camera picks up from
 * across a room, and eight labels a sheet. Denser grids are a setting away for
 * anyone labelling small boxes.
 */
export const DEFAULT_LAYOUT: LabelLayout = { pageSize: 'letter', columns: 2, rows: 4 };

export const COLUMN_RANGE = { min: 1, max: 6 };
export const ROW_RANGE = { min: 1, max: 8 };

export interface Geometry {
  page: PageSize;
  /** The printable area, i.e. the page inside the @page margin. */
  contentWidthMm: number;
  contentHeightMm: number;
  cellWidthMm: number;
  cellHeightMm: number;
  perPage: number;
  qrMm: number;
  /** Font size for the printed code, in mm - sized so a full XXX-XXX always fits its cell. */
  codeMm: number;
  /** Narrow cells stack the code under the QR instead of beside it. */
  stacked: boolean;
}

export function geometryFor(layout: LabelLayout): Geometry {
  const page = PAGE_SIZES.find((size) => size.id === layout.pageSize) ?? PAGE_SIZES[0];
  const columns = clamp(layout.columns, COLUMN_RANGE);
  const rows = clamp(layout.rows, ROW_RANGE);

  const contentWidthMm = page.widthMm - 2 * MARGIN_MM;
  const contentHeightMm = page.heightMm - 2 * MARGIN_MM;
  const cellWidthMm = contentWidthMm / columns;
  const cellHeightMm = contentHeightMm / rows;

  const stacked = cellWidthMm < 60;
  const qrMm = stacked
    ? Math.min(cellWidthMm - 2 * PAD_MM, (cellHeightMm - 3 * PAD_MM) * 0.62)
    : Math.min(cellHeightMm - 2 * PAD_MM, (cellWidthMm - 3 * PAD_MM) * 0.5);

  // The code has to fit whatever room the QR left. A 7-character mono string is
  // about 4.2 ems wide, so that ratio is the ceiling; below ~3mm it stops being
  // readable at arm's length, which is the whole reason it's printed.
  const textWidthMm = stacked ? cellWidthMm - 2 * PAD_MM : cellWidthMm - qrMm - 3 * PAD_MM;
  const codeMm = Math.max(3, Math.min(qrMm * 0.2, 8, textWidthMm / 4.2));

  return {
    page,
    contentWidthMm,
    contentHeightMm,
    cellWidthMm,
    cellHeightMm,
    perPage: columns * rows,
    qrMm,
    codeMm,
    stacked,
  };
}

const STORAGE_KEY = 'aerie.storage.labelLayout';

/**
 * Layout is a property of someone's printer and paper drawer, not of their
 * data, so it lives in localStorage rather than the server: the same household
 * can print from a laptop on Letter and a tablet on A4 without either winning.
 */
export function useLabelLayout(): [LabelLayout, (patch: Partial<LabelLayout>) => void] {
  const [layout, setLayout] = useState<LabelLayout>(readLayout);

  const update = useCallback((patch: Partial<LabelLayout>) => {
    setLayout((previous) => {
      const next = normalize({ ...previous, ...patch });
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
      } catch {
        // Private mode, or a full quota. A forgotten setting is not worth a failed render.
      }
      return next;
    });
  }, []);

  return [layout, update];
}

function readLayout(): LabelLayout {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null');
    return stored ? normalize(stored as Partial<LabelLayout>) : DEFAULT_LAYOUT;
  } catch {
    return DEFAULT_LAYOUT;
  }
}

/** Anything stored is anything typed, and a bad number here is a blank sheet of paper. */
function normalize(layout: Partial<LabelLayout>): LabelLayout {
  return {
    pageSize: PAGE_SIZES.some((size) => size.id === layout.pageSize) ? layout.pageSize! : DEFAULT_LAYOUT.pageSize,
    columns: clamp(layout.columns ?? DEFAULT_LAYOUT.columns, COLUMN_RANGE),
    rows: clamp(layout.rows ?? DEFAULT_LAYOUT.rows, ROW_RANGE),
  };
}

function clamp(value: number, { min, max }: { min: number; max: number }): number {
  return Number.isFinite(value) ? Math.min(max, Math.max(min, Math.round(value))) : min;
}

export function chunk<T>(items: T[], size: number): T[][] {
  const pages: T[][] = [];
  for (let i = 0; i < items.length; i += size) pages.push(items.slice(i, i + size));
  return pages;
}
