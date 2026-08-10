import { useMemo } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { createCrate, getCrates } from './api';
import { CodeChip, EmptyNote, ErrorNote, InlineError, Loading } from './components';
import { cratePath, labelsPath } from './routes';
import type { Crate } from './types';
import { useMutation, useResource } from './useResource';

/**
 * Every crate, grouped by where it is - the "what's in the attic" question,
 * which is the other half of the index screen's "where is the drill".
 *
 * Unplaced crates are their own group at the end rather than hidden: a crate
 * with no location is usually one that was just minted, or one whose shelf got
 * deleted, and both are things to notice rather than lose.
 */
export function CrateListPage() {
  const crates = useResource<Crate[]>('crates', getCrates);
  const groups = useMemo(() => groupByLocation(crates.data ?? []), [crates.data]);

  if (crates.loading && !crates.data) return <Loading />;
  if (crates.error) return <ErrorNote message={crates.error} onRetry={crates.reload} />;

  return (
    <>
      <NewCrateButton />

      {groups.length === 0 ? (
        <EmptyNote>No crates yet.</EmptyNote>
      ) : (
        groups.map((group) => (
          <section key={group.name} className="storage-group">
            <h3 className="storage-group-title">
              {group.name} <span className="text-muted">· {group.crates.length}</span>
            </h3>
            <ul className="storage-list">
              {group.crates.map((crate) => (
                <li key={crate.id}>
                  <Link to={cratePath(crate.id)} className="storage-row">
                    <span className="storage-row-text">
                      <span className="storage-row-title">
                        <CodeChip code={crate.displayCode} />
                        {crate.label ?? <span className="text-muted">Unlabelled</span>}
                      </span>
                      <span className="storage-row-sub">
                        {crate.itemCount} {crate.itemCount === 1 ? 'item' : 'items'}
                      </span>
                    </span>
                    <span className="storage-row-chevron" aria-hidden="true">
                      ›
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          </section>
        ))
      )}
    </>
  );
}

/**
 * Mints one blank crate and opens it, for the case where a box is already in
 * front of you and its label gets written by hand. The workflow that matters -
 * batch-create, print a sheet, tape, then scan each one as it's filled - is
 * next to it, on LabelsPage.
 */
function NewCrateButton() {
  const create = useMutation();
  const navigate = useNavigate();

  async function mint() {
    // Navigating inside the operation rather than after it is what keeps the
    // failure path honest: a throw skips the navigate and lands in create.error.
    await create.run(async () => {
      const detail = await createCrate({ label: null, locationId: null, notes: null });
      navigate(cratePath(detail.crate.id));
    });
  }

  return (
    <div className="storage-toolbar storage-form-actions">
      <button className="btn-primary" onClick={mint} disabled={create.busy}>
        {create.busy ? 'Creating…' : 'New crate'}
      </button>
      <Link to={labelsPath} className="storage-link-btn">
        Print labels
      </Link>
      {create.error && <InlineError message={create.error} />}
    </div>
  );
}

interface CrateGroup {
  name: string;
  crates: Crate[];
}

/**
 * The server already orders placed crates by location name and unplaced ones
 * last, so this is a run-length pass rather than a sort - grouping here can't
 * disagree with the order rows arrive in.
 */
function groupByLocation(crates: Crate[]): CrateGroup[] {
  const groups: CrateGroup[] = [];
  for (const crate of crates) {
    const name = crate.locationName ?? 'Not in a location';
    const last = groups.at(-1);
    if (last?.name === name) last.crates.push(crate);
    else groups.push({ name, crates: [crate] });
  }
  return groups;
}
