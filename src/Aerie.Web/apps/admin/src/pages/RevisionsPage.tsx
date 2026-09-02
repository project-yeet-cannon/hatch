import { useCallback, useEffect, useState } from 'react';
import { Badge, PageHeader, Table } from '@aerie/ui';
import { getAerieRevision } from '../api/client';
import type { AerieRevisionInfo, FluxSourceRevision, RevisionDrift } from '../types';

/**
 * Every revision in the system on one page: what this API replica is running,
 * what this browser is running, and what Flux has actually reconciled.
 *
 * The gap between the last two is the reason the page exists. What has been
 * pushed and what the cluster is running are different facts, and until now the
 * difference was visible only to someone holding a kubeconfig - so "is the new
 * build out yet" was answered by waiting and guessing. See docs/plans/version.md.
 *
 * It polls rather than snapshotting, because the interesting moment is the one
 * where the numbers disagree and then converge, and that moment is short.
 *
 * The API replica line is worth reading twice during a rollout: with three
 * replicas behind one Service, consecutive polls can legitimately report
 * different revisions, and that *is* the answer - the rollout is in flight.
 */
const POLL_INTERVAL_MS = 10_000;

export function RevisionsPage() {
  const [info, setInfo] = useState<AerieRevisionInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    try {
      setInfo(await getAerieRevision());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
    const timer = setInterval(() => void load(), POLL_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [load]);

  if (loading) return <p>Loading revisions…</p>;
  if (error && !info) return <p className="admin-crash-note">Could not read revisions: {error}</p>;
  if (!info) return null;

  return (
    <div>
      <PageHeader
        title="Revisions"
        description={`Which commit each part of the system is running. Refreshes every ${POLL_INTERVAL_MS / 1000}s.`}
      />

      {error && <p className="admin-crash-note">Last refresh failed: {error}</p>}

      <Table>
        <thead>
          <tr>
            <th>What</th>
            <th>Revision</th>
            <th>Sequence</th>
            <th>State</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>API replica{info.builtAt ? ` · built ${new Date(info.builtAt).toLocaleString()}` : ''}</td>
            <td><Sha value={info.revision} /></td>
            <td>{info.sequence || '—'}</td>
            <td>{info.revision === 'dev' ? <Badge tone="muted">dev build</Badge> : <Badge tone="success">serving</Badge>}</td>
          </tr>
          <tr>
            <td>This browser</td>
            <td><Sha value={info.client?.revision ?? null} /></td>
            <td>{info.client?.sequence || '—'}</td>
            <td>{info.client ? <DriftBadge drift={info.client.drift} /> : <Badge tone="muted">not reported</Badge>}</td>
          </tr>
        </tbody>
      </Table>

      <ClusterSection cluster={info.cluster} />
    </div>
  );
}

function ClusterSection({ cluster }: { cluster: AerieRevisionInfo['cluster'] }) {
  // Null for an unauthenticated caller. Distinguished from "read and empty" on
  // purpose - "you cannot see this" and "there is nothing there" are different
  // answers, and collapsing them would send someone hunting for a broken Flux.
  if (!cluster) {
    return <PageHeader title="Cluster" level={2} description="Sign in to see what Flux has reconciled." />;
  }

  if (cluster.unavailable) {
    return <PageHeader title="Cluster" level={2} description={`Flux could not be read: ${cluster.unavailable}.`} />;
  }

  return (
    <>
      <PageHeader
        title="Cluster"
        level={2}
        description="What Flux has fetched, and what each Kustomization has actually applied from it."
      />
      {cluster.sources.map((source) => <SourceTable key={source.name} source={source} />)}
    </>
  );
}

function SourceTable({ source }: { source: FluxSourceRevision }) {
  return (
    <div>
      <h3>
        {source.name}
        {source.branch ? ` · ${source.branch}` : ''} <Sha value={source.revision} />
      </h3>
      <Table>
        <thead>
          <tr>
            <th>Kustomization</th>
            <th>Applied</th>
            <th>State</th>
          </tr>
        </thead>
        <tbody>
          {source.kustomizations.length === 0 && (
            <tr><td colSpan={3}>Nothing reconciles from this source.</td></tr>
          )}
          {source.kustomizations.map((k) => {
            // Behind its own source means a reconcile is in flight or stuck.
            // Which of those it is depends on Ready, not on the sha - a
            // Kustomization can sit ready at an old revision for a long time
            // while a dependency ahead of it refuses to become healthy.
            const converged = k.appliedRevision !== null && k.appliedRevision === source.revision;
            return (
              <tr key={k.name}>
                <td>{k.name}</td>
                <td><Sha value={k.appliedRevision} /></td>
                <td>
                  {k.ready === false && <Badge tone="muted">not ready</Badge>}
                  {k.ready !== false && converged && <Badge tone="success">converged</Badge>}
                  {k.ready !== false && !converged && <Badge tone="muted">behind source</Badge>}
                </td>
              </tr>
            );
          })}
        </tbody>
      </Table>
    </div>
  );
}

/**
 * Shas are shown short and carry the full value in the title, because 40
 * characters of hex in a table cell is a column nobody can scan and the first
 * seven are what anyone actually compares by eye.
 */
function Sha({ value }: { value: string | null }) {
  if (!value) return <>—</>;
  if (value === 'dev') return <>dev</>;
  return <code title={value}>{value.slice(0, 7)}</code>;
}

function DriftBadge({ drift }: { drift: RevisionDrift }) {
  const label: Record<RevisionDrift, string> = {
    Current: 'up to date',
    // Not an error. Mid-rollout a page loaded from a new replica can be answered
    // by an old one, which is exactly what this says and exactly why nothing
    // reloads on it.
    Ahead: 'ahead of this replica',
    Behind: 'reload to update',
    Unknown: 'not comparable',
  };
  return <Badge tone={drift === 'Current' ? 'success' : 'muted'}>{label[drift]}</Badge>;
}
