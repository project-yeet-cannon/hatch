import { useCallback, useState } from 'react';

/*
  The geometry of a label sheet, kept out of the component because it's
  arithmetic rather than markup.

  Everything here is in millimetres, including on screen: CSS `mm` is a real
  physical unit at print time, so one set of numbers describes the preview and
  the paper.

  Two kinds of sheet share this arithmetic. Plain paper is the original: any
  page size, any grid, dashed cut lines, and the user decides how dense it is.
  A die-cut stock (Avery and friends) fixes all of that in the paper itself -
  the grid is already cut, so the numbers stop being a preference and start
  being a specification that has to be matched to a fraction of a millimetre.
  The difference between the two is entirely in LabelStock; the geometry below
  doesn't know which it's looking at.
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

export interface LabelStock {
  id: string;
  label: string;
  /** Fixed by the product, or null when the user picks the paper. */
  pageSizeId: string | null;
  /** Fixed by the die, or null when the user picks the grid. */
  grid: { columns: number; rows: number } | null;
  /** Page edge to the first label, left/right and top/bottom. */
  marginXMm: number;
  marginYMm: number;
  /** Bare paper between adjacent labels. Zero on plain paper: cells share a cut line. */
  gapXMm: number;
  gapYMm: number;
  /** Label edge to anything printed on it - the quiet zone and the die-cut safe area. */
  padMm: number;
  /** Plain paper needs a line to cut on; a die-cut sheet already has one. */
  cutLines: boolean;
  /** Rounded die corners, drawn in the preview so the sticker looks like the sticker. */
  cornerRadiusMm: number;
  /** Shown under the stock picker, where the units are the ones on the box. */
  note: string;
}

/**
 * Plain paper: the sheet is paper, cut lines and tape. 10mm margins are
 * printer-safe on both page sizes and wide enough that a cut line isn't at the
 * edge; 3mm of padding keeps the QR's quiet zone off the scissors.
 */
const PLAIN_PAPER: LabelStock = {
  id: 'plain',
  label: 'Plain paper (cut out)',
  pageSizeId: null,
  grid: null,
  marginXMm: 10,
  marginYMm: 10,
  gapXMm: 0,
  gapYMm: 0,
  padMm: 3,
  cutLines: true,
  cornerRadiusMm: 0,
  note: 'Any paper. Cut on the dashed lines and tape them on.',
};

/*
  Avery 22816: 2" square print-to-the-edge labels, 12 to a Letter sheet, 3
  across and 4 down. The die tiles the page exactly, which is the arithmetic
  worth writing down because it's also the check:

    across  0.625" + 3x2" + 2x0.625" + 0.625"  =  8.5"
    down    0.6"   + 4x2" + 3x0.6"   + 0.6"    =  11"

  so the margins and the gaps between labels are the same measurement in each
  direction, and the content box below lands on the die rather than near it.

  4mm of padding rather than plain paper's 3: these have rounded corners and a
  real die-cut edge, and a printer that feeds a hair off-square eats whatever
  sits in that last millimetre.
*/
const AVERY_22816: LabelStock = {
  id: 'avery-22816',
  label: 'Avery 22816 (2" square)',
  pageSizeId: 'letter',
  grid: { columns: 3, rows: 4 },
  marginXMm: 15.875,
  marginYMm: 15.24,
  gapXMm: 15.875,
  gapYMm: 15.24,
  padMm: 4,
  cutLines: false,
  cornerRadiusMm: 3.2,
  note: '2" square stickers, 12 per Letter sheet. Peel and stick - no cutting.',
};

export const LABEL_STOCKS: LabelStock[] = [PLAIN_PAPER, AVERY_22816];

export function stockFor(id: string | undefined): LabelStock {
  return LABEL_STOCKS.find((stock) => stock.id === id) ?? PLAIN_PAPER;
}

export interface LabelLayout {
  stock: string;
  /** Only consulted when the stock doesn't fix the paper. */
  pageSize: string;
  /** Only consulted when the stock doesn't fix the grid. */
  columns: number;
  rows: number;
}

/**
 * Two by four on plain Letter: a ~44mm QR, which the stock iOS camera picks up
 * from across a room, and eight labels a sheet. Denser grids - and a sheet of
 * stickers - are a setting away.
 */
export const DEFAULT_LAYOUT: LabelLayout = { stock: 'plain', pageSize: 'letter', columns: 2, rows: 4 };

export const COLUMN_RANGE = { min: 1, max: 6 };
export const ROW_RANGE = { min: 1, max: 8 };

/** A 7-character mono code at weight 700 with 0.06em tracking is about this many ems wide. */
const CODE_EM_WIDTH = 4.2;

/** Between the code and the name/write-on line under it. Mirrored by `.label-text` in labels.css. */
const TEXT_LINE_GAP_MM = 1.5;

/** Below this the printed code stops being readable at arm's length, which is the point of printing it. */
const CODE_MIN_MM = 3;
const CODE_MAX_MM = 8;

export interface Geometry {
  stock: LabelStock;
  page: PageSize;
  columns: number;
  rows: number;
  /** The printable area, i.e. the page inside the @page margin. */
  contentWidthMm: number;
  contentHeightMm: number;
  cellWidthMm: number;
  cellHeightMm: number;
  perPage: number;
  qrMm: number;
  /** Font size for the printed code, in mm. */
  codeMm: number;
  /** Narrow cells stack the code under the QR instead of beside it. */
  stacked: boolean;
}

export function geometryFor(layout: LabelLayout): Geometry {
  const stock = stockFor(layout.stock);
  const page =
    PAGE_SIZES.find((size) => size.id === (stock.pageSizeId ?? layout.pageSize)) ?? PAGE_SIZES[0];
  const columns = stock.grid ? stock.grid.columns : clamp(layout.columns, COLUMN_RANGE);
  const rows = stock.grid ? stock.grid.rows : clamp(layout.rows, ROW_RANGE);

  const contentWidthMm = page.widthMm - 2 * stock.marginXMm;
  const contentHeightMm = page.heightMm - 2 * stock.marginYMm;
  const cellWidthMm = (contentWidthMm - (columns - 1) * stock.gapXMm) / columns;
  const cellHeightMm = (contentHeightMm - (rows - 1) * stock.gapYMm) / rows;

  const stacked = cellWidthMm < 60;
  const { qrMm, codeMm } = fitCell(
    cellWidthMm - 2 * stock.padMm,
    cellHeightMm - 2 * stock.padMm,
    stacked,
    stock.padMm,
  );

  return {
    stock,
    page,
    columns,
    rows,
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

/**
 * Divides a label's usable area between the QR and the text under or beside it.
 *
 * The code is sized first and the QR takes what's left, rather than the other
 * way round: the code has a hard floor below which it stops doing its job, and
 * a QR that's a few millimetres smaller still scans. Solving for a fit rather
 * than scaling by a fudge factor is what lets a 2" square - where the two
 * demands actually collide - come out with both intact.
 */
function fitCell(availWidthMm: number, availHeightMm: number, stacked: boolean, padMm: number) {
  if (stacked) {
    const codeMm = clampCode(Math.min(availWidthMm / CODE_EM_WIDTH, availHeightMm * 0.12));
    const qrMm = Math.max(0, Math.min(availWidthMm, availHeightMm - padMm - textBlockMm(codeMm)));
    return { qrMm, codeMm };
  }

  const qrMm = Math.max(0, Math.min(availHeightMm, (availWidthMm - padMm) * 0.5));
  const codeMm = clampCode(
    Math.min((availWidthMm - padMm - qrMm) / CODE_EM_WIDTH, (availHeightMm - TEXT_LINE_GAP_MM) / 2.4),
  );
  return { qrMm, codeMm };
}

/**
 * Height of the code plus the line under it. Sized for the write-on line, which
 * is the taller of the two cases and the one a blank crate gets - and blank
 * crates are the workflow this screen exists for.
 */
const textBlockMm = (codeMm: number) => codeMm * 2.4 + TEXT_LINE_GAP_MM;

const clampCode = (mm: number) => Math.max(CODE_MIN_MM, Math.min(CODE_MAX_MM, mm));

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

/**
 * Anything stored is anything typed, and a bad number here is a blank sheet of
 * paper - or, now, a blank sheet of stickers. A layout written before stocks
 * existed has no `stock` and falls through to plain paper, which is what it was.
 */
function normalize(layout: Partial<LabelLayout>): LabelLayout {
  return {
    stock: stockFor(layout.stock).id,
    pageSize: PAGE_SIZES.some((size) => size.id === layout.pageSize)
      ? layout.pageSize!
      : DEFAULT_LAYOUT.pageSize,
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
