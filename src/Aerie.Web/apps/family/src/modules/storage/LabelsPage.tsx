import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useAppConfig } from '../../lib/appConfig';
import { createCrateBatch, getCrates, MAX_BATCH_COUNT } from './api';
import { EmptyNote, ErrorNote, InlineError, Loading } from './components';
import { LabelSheet } from './LabelSheet';
import {
  COLUMN_RANGE,
  geometryFor,
  PAGE_SIZES,
  ROW_RANGE,
  useLabelLayout,
} from './labelLayout';
import type { LabelLayout } from './labelLayout';
import { useQrCodes } from './qr';
import { crateLabelUrl, cratesPath, labelsPath, reprintPath } from './routes';
import type { Crate } from './types';
import { useMutation, useResource } from './useResource';
import './labels.css';

/**
 * The label printer, and the workflow the app is actually built around: mint N
 * blank crates, print the sheet, tape them onto empty boxes, then scan each one
 * as it gets filled. Naming a crate in the app before the box exists is
 * backwards, which is why "New crate" is the small path and this is the big one.
 *
 * Which crates a sheet is for lives in the URL (`?code=…`), not in state. That
 * is what makes a minted batch survive a refresh: paper has been committed to
 * by the time this screen matters, and a lost list of codes means blank boxes
 * with no way back to them.
 */
export function LabelsPage() {
  const [params] = useSearchParams();
  const codes = [...new Set(params.getAll('code').map(normalizeCode).filter(Boolean))];

  return codes.length === 0 ? <NewLabelsForm /> : <PrintView codes={codes} />;
}

/** Strips what a hand-edited or copied URL might carry; our own links are already bare. */
function normalizeCode(raw: string): string {
  return raw.trim().toUpperCase().replace(/[^0-9A-Z]/g, '');
}

function NewLabelsForm() {
  const [layout, updateLayout] = useLabelLayout();
  const geometry = geometryFor(layout);
  const [count, setCount] = useState(String(geometry.perPage));
  const create = useMutation();
  const navigate = useNavigate();

  const requested = Number.parseInt(count, 10);
  const valid = Number.isFinite(requested) && requested >= 1 && requested <= MAX_BATCH_COUNT;

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!valid) return;

    await create.run(async () => {
      const crates = await createCrateBatch(requested);
      // Replace rather than push, and hand the codes to the URL: back from the
      // sheet goes where the user came from, and a refresh still finds the
      // crates that now exist.
      navigate(reprintPath(crates.map((crate) => crate.code)), { replace: true, state: { autoPrint: true } });
    });
  }

  return (
    <form className="card storage-form" onSubmit={submit}>
      <p className="labels-lede">
        Print a sheet of blank labels, tape them onto empty boxes, then scan each one as you fill it.
      </p>

      <label className="storage-field">
        <span>How many labels</span>
        <input
          type="text"
          inputMode="numeric"
          value={count}
          onChange={(e) => setCount(e.target.value)}
          aria-label="How many labels"
        />
      </label>

      <LayoutControls layout={layout} onChange={updateLayout} />

      <p className="labels-hint">
        {geometry.perPage} per sheet · {valid ? `${sheetCount(requested, geometry.perPage)} sheet(s)` : `1–${MAX_BATCH_COUNT}`}
      </p>

      {create.error && <InlineError message={create.error} />}

      <div className="storage-form-actions">
        <button type="submit" className="btn-primary" disabled={create.busy || !valid}>
          {create.busy ? 'Creating…' : 'Create crates & print'}
        </button>
        <Link to={cratesPath} className="storage-link-btn">
          All crates
        </Link>
      </div>
    </form>
  );
}

/**
 * The sheet for a known set of codes - freshly minted, or an old crate whose
 * label went through a decade of garage. Crates come from the full list rather
 * than N lookups: it's one request, and it's the one the crate list has already
 * put in the service worker's cache.
 */
function PrintView({ codes }: { codes: string[] }) {
  const crates = useResource<Crate[]>(`labels:${codes.join(',')}`, async (signal) => {
    const all = await getCrates(signal);
    const byCode = new Map(all.map((crate) => [crate.code, crate]));
    return codes.map((code) => byCode.get(code)).filter((crate): crate is Crate => crate !== undefined);
  });

  const [layout, updateLayout] = useLabelLayout();
  const { config, loading: configLoading } = useAppConfig();
  const autoPrint = (useLocation().state as { autoPrint?: boolean } | null)?.autoPrint === true;

  // The install's canonical URL if it's configured, and this browser's origin
  // if it isn't. Falling back keeps labels printable on a fresh install; the
  // notice below is what stops that fallback from being a silent decision
  // discovered years later, when the printing laptop's hostname is on tape.
  const baseUrl = config?.publicBaseUrl ?? window.location.origin;
  const found = crates.data ?? [];
  const { images, error: qrError } = useQrCodes(
    // Don't render QRs against a base that's about to change under them.
    configLoading ? [] : found.map((crate) => crateLabelUrl(baseUrl, crate.code)),
  );

  const printed = useRef(false);
  useEffect(() => {
    if (!autoPrint || printed.current || !images || found.length === 0) return;
    printed.current = true;
    // One frame so the browser has painted the sheet the dialog previews.
    const frame = requestAnimationFrame(() => window.print());
    return () => cancelAnimationFrame(frame);
  }, [autoPrint, images, found.length]);

  if (crates.loading && !crates.data) return <Loading />;
  if (crates.error) return <ErrorNote message={crates.error} onRetry={crates.reload} />;

  const missing = codes.length - found.length;
  const geometry = geometryFor(layout);

  return (
    <>
      <div className="labels-controls">
        <div className="storage-form-actions labels-actions">
          <button className="btn-primary" onClick={() => window.print()} disabled={!images || found.length === 0}>
            {images ? 'Print sheet' : 'Drawing codes…'}
          </button>
          <Link to={labelsPath} className="storage-link-btn">
            New labels
          </Link>
        </div>

        <p className="labels-hint">
          {found.length} {found.length === 1 ? 'label' : 'labels'} ·{' '}
          {sheetCount(found.length, geometry.perPage)} sheet(s) · cut on the dashed lines
        </p>

        {missing > 0 && (
          <p className="labels-hint">
            {missing} of these crates no longer {missing === 1 ? 'exists' : 'exist'}.
          </p>
        )}

        {!configLoading && !config?.publicBaseUrl && (
          <p className="labels-warning" role="status">
            These labels will point at <code>{baseUrl}</code>. Set <code>Apps:PublicBaseUrl</code> so they keep
            working if this machine's address changes — they'll be on tape long after it does.
          </p>
        )}

        {qrError && <InlineError message={`Couldn't draw the codes: ${qrError}`} />}

        <LayoutControls layout={layout} onChange={updateLayout} />
      </div>

      {found.length === 0 ? (
        <EmptyNote>Nothing to print.</EmptyNote>
      ) : (
        images && <LabelSheet crates={found} layout={layout} baseUrl={baseUrl} images={images} />
      )}
    </>
  );
}

/** Paper and grid. Deliberately not a label-product picker: this is plain paper and scissors. */
function LayoutControls({ layout, onChange }: { layout: LabelLayout; onChange: (patch: Partial<LabelLayout>) => void }) {
  return (
    <div className="labels-layout">
      <label className="storage-field">
        <span>Paper</span>
        <select value={layout.pageSize} onChange={(e) => onChange({ pageSize: e.target.value })}>
          {PAGE_SIZES.map((size) => (
            <option key={size.id} value={size.id}>
              {size.label}
            </option>
          ))}
        </select>
      </label>

      <label className="storage-field">
        <span>Columns</span>
        <input
          type="number"
          min={COLUMN_RANGE.min}
          max={COLUMN_RANGE.max}
          value={layout.columns}
          onChange={(e) => onChange({ columns: Number(e.target.value) })}
        />
      </label>

      <label className="storage-field">
        <span>Rows</span>
        <input
          type="number"
          min={ROW_RANGE.min}
          max={ROW_RANGE.max}
          value={layout.rows}
          onChange={(e) => onChange({ rows: Number(e.target.value) })}
        />
      </label>
    </div>
  );
}

const sheetCount = (total: number, perPage: number) => Math.max(1, Math.ceil(total / perPage));
