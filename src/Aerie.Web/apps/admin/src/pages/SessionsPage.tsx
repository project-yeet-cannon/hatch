import { useEffect, useRef, useState } from 'react';
import QRCode from 'qrcode';
import { Modal } from '../components/Modal';
import { createInvite, deleteGrant, getAppsConfig, getGrants, getPeople, linkGrantPerson, signOutDevice } from '../api/client';
import { formatAge } from '../lib/format';
import type { AuthGrant, AuthInvite, Person } from '../types';

/**
 * Every enrolled device, and the one button that enrols another.
 *
 * A session here is a *grant*: one long-lived credential held by one device,
 * created by redeeming an invite and revoked by deleting the row. There is no
 * password anywhere in this flow and nothing to rotate - which is the point of
 * the page, since the alternative a household reaches for is one shared secret
 * that can't be taken back from a single phone.
 *
 * The caller's own row is marked rather than deletable. The server refuses that
 * DELETE too (AuthController.RevokeGrant): deleting your own row leaves this
 * browser holding a cookie no grant answers to, which is precisely the state
 * that makes "sign out and back in" fail to fix anything. Sign out does both
 * halves, so that row offers that instead.
 *
 * Whose device it is is edited here rather than on the People page, and this is
 * the page that owns that write. A session has at most one person, so it is one
 * select on a row that already exists; a person has any number of sessions, so
 * the inverse would be a multi-picker. It is also the moment anyone actually
 * cares - you are looking at this table while enrolling the tablet - which is
 * why the invite carries a person too, and the link is already set by the time
 * the row appears.
 *
 * Nothing about the wall changes with it. The dropdown writes a name onto a
 * credential; no gate reads the column.
 */
export function SessionsPage() {
  const [grants, setGrants] = useState<AuthGrant[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [label, setLabel] = useState('');
  // Who the *next* invite is for. Separate from the per-row dropdowns because
  // it describes a device that does not exist yet.
  const [invitePersonId, setInvitePersonId] = useState('');
  const [generating, setGenerating] = useState(false);
  const [invite, setInvite] = useState<AuthInvite | null>(null);
  // The install's canonical origin, or null when Apps:PublicBaseUrl is unset or
  // unreachable. Either way the QR still works from this browser's own origin -
  // it just carries whatever address this tab happens to be using.
  const [publicBaseUrl, setPublicBaseUrl] = useState<string | null>(null);

  useEffect(() => {
    load();
    getAppsConfig().then(
      (config) => setPublicBaseUrl(config.publicBaseUrl),
      () => setPublicBaseUrl(null),
    );
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      // Together, because a Person column that renders before the names arrive
      // is a column of blanks that looks like unclaimed devices.
      const [loadedGrants, loadedPeople] = await Promise.all([getGrants(), getPeople()]);
      setGrants(loadedGrants);
      setPeople(loadedPeople);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function generate() {
    setGenerating(true);
    setError(null);
    try {
      setInvite(await createInvite(label.trim() || null, invitePersonId || null));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setGenerating(false);
    }
  }

  async function link(grant: AuthGrant, personId: string | null) {
    setError(null);
    // Written straight into the row rather than re-fetching the table: the
    // answer is already known, and a full reload would collapse whatever else
    // someone was in the middle of reading.
    const previous = grants;
    setGrants((prev) =>
      prev.map((g) =>
        g.id === grant.id ? { ...g, personId, personName: people.find((p) => p.id === personId)?.name ?? null } : g,
      ),
    );

    try {
      await linkGrantPerson(grant.id, personId);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      // Put the table back. A dropdown showing a link the server refused is
      // worse than the error, because it goes on being wrong after the message
      // is dismissed.
      setGrants(previous);
    }
  }

  async function revoke(grant: AuthGrant) {
    if (!confirm(`Revoke "${grant.label}"? It signs out on its next request and needs a new code to come back.`)) return;

    setError(null);
    try {
      await deleteGrant(grant.id);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function signOutThisDevice() {
    if (!confirm('Sign out this device? You will need a new invite code to sign back in.')) return;

    setError(null);
    try {
      await signOutDevice();
      // Not a re-fetch: the cookie is gone, so the next request from this tab is
      // the one that meets the wall. Reloading is what lets it, rather than
      // leaving a page full of buttons that will all fail.
      window.location.reload();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  return (
    <div>
      <div className="admin-page-header">
        <h2>Sessions</h2>
        <div className="flex gap-1" style={{ alignItems: 'center' }}>
          <input
            type="text"
            placeholder="Device name (optional)"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
          />
          {/* Whose device this is about to be. Set here and the link is already
              in place when the row appears, rather than being a second step
              somebody has to remember after the tablet is in a stranger's
              hands. */}
          <PersonSelect
            people={people}
            value={invitePersonId || null}
            unclaimedLabel="Nobody in particular"
            onChange={(personId) => setInvitePersonId(personId ?? '')}
          />
          <button className="btn-primary" disabled={generating} onClick={generate}>
            {generating ? 'Generating…' : 'Generate invite'}
          </button>
          <button className="btn-secondary" onClick={load}>
            Refresh
          </button>
        </div>
      </div>

      {error && <p className="text-danger mb-2">{error}</p>}
      {loading && <p className="text-muted">Loading…</p>}

      {!loading && grants.length === 0 && (
        <div className="card">
          <p>
            No devices are enrolled. <strong>Generate invite</strong> makes a code that turns one browser into a
            session — good for fifteen minutes, and good exactly once.
          </p>
        </div>
      )}

      {!loading && grants.length > 0 && (
        <div className="card">
          <table className="admin-table">
            <thead>
              <tr>
                <th>Device</th>
                <th>Person</th>
                <th>Kind</th>
                <th>Enrolled</th>
                <th>Last seen</th>
                <th>Last address</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {grants.map((grant) => (
                <tr key={grant.id}>
                  <td>
                    <div className="flex gap-1" style={{ alignItems: 'center' }}>
                      {/* The user agent is the name's provenance, not the name -
                          it earns a tooltip and nothing more. */}
                      <span title={grant.userAgent ?? undefined}>{grant.label}</span>
                      {grant.isCurrent && <span className="badge badge-success">This device</span>}
                    </div>
                  </td>
                  <td>
                    <PersonSelect
                      people={people}
                      value={grant.personId}
                      unclaimedLabel="Unclaimed"
                      onChange={(personId) => link(grant, personId)}
                    />
                  </td>
                  <td>{grant.kind}</td>
                  <td>{formatAge(grant.createdAt)}</td>
                  <td>{grant.lastSeenAt ? formatAge(grant.lastSeenAt) : 'Never'}</td>
                  <td>{grant.lastSeenIp ?? '—'}</td>
                  <td>
                    {grant.isCurrent ? (
                      <button className="btn-secondary" onClick={signOutThisDevice}>
                        Sign out
                      </button>
                    ) : (
                      <button className="btn-danger" onClick={() => revoke(grant)}>
                        Revoke
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <InviteModal invite={invite} publicBaseUrl={publicBaseUrl} onClose={() => setInvite(null)} />
    </div>
  );
}

/**
 * Which person a device belongs to, or nobody.
 *
 * "Nobody" is a first-class choice rather than an absence, and it is worded for
 * where it appears: an unclaimed *device* on a row, an unspecified *person* on
 * the invite. The empty option is always present because unclaiming has to be
 * as easy as claiming - a household tablet that was briefly assigned to whoever
 * set it up is the ordinary case, not an error to be recovered from.
 */
function PersonSelect({
  people,
  value,
  unclaimedLabel,
  onChange,
}: {
  people: Person[];
  value: string | null;
  unclaimedLabel: string;
  onChange: (personId: string | null) => void;
}) {
  return (
    <select
      value={value ?? ''}
      // Disabled rather than hidden when there is nobody to pick: a missing
      // control reads as a missing feature, where a disabled one with this
      // title says where to go.
      disabled={people.length === 0}
      title={people.length === 0 ? 'Add someone on the People page first' : undefined}
      onChange={(e) => onChange(e.target.value || null)}
    >
      <option value="">{unclaimedLabel}</option>
      {people.map((person) => (
        <option key={person.id} value={person.id}>
          {person.name}
        </option>
      ))}
    </select>
  );
}

/**
 * The code, once. It is not stored anywhere in plaintext, so closing this is
 * the end of it - a lost code is replaced by generating another, never by
 * looking this one up.
 *
 * Both ways of handing it over are here on purpose: the QR for a phone that can
 * point a camera at the screen, and the same code in large type for a tablet
 * across the room being told it out loud. The second is why the alphabet has no
 * I, L, O or U in it, and it is what makes in-app QR scanning on the kiosks
 * optional rather than required.
 */
function InviteModal({
  invite,
  publicBaseUrl,
  onClose,
}: {
  invite: AuthInvite | null;
  publicBaseUrl: string | null;
  onClose: () => void;
}) {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [now, setNow] = useState(() => Date.now());
  const [qrError, setQrError] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const redeemUrl = invite ? `${publicBaseUrl ?? window.location.origin}${invite.redeemPath}` : '';
  const remaining = invite ? new Date(invite.expiresAt).getTime() - now : 0;
  const expired = remaining <= 0;

  useEffect(() => {
    if (!invite) return;

    setNow(Date.now());
    setCopied(false);
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [invite]);

  useEffect(() => {
    if (!invite || !canvasRef.current) return;

    setQrError(null);
    QRCode.toCanvas(canvasRef.current, redeemUrl, { width: 280 }).catch((err: unknown) =>
      setQrError(err instanceof Error ? err.message : String(err)),
    );
  }, [invite, redeemUrl]);

  async function copyLink() {
    await navigator.clipboard.writeText(redeemUrl);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  }

  return (
    <Modal open={invite !== null} onClose={onClose} title="Enrol a device">
      {invite && (
        <div className="invite">
          <p className="text-muted">
            Scan this, or read the code out. Either one signs a device in{invite.label ? ` as “${invite.label}”` : ''}{' '}
            and keeps it signed in until you revoke it here.
          </p>

          <div className="invite-qr" aria-hidden={expired}>
            <canvas ref={canvasRef} style={expired ? { opacity: 0.25 } : undefined} />
          </div>

          <p className="invite-code">{invite.formattedCode}</p>

          <p className={expired ? 'text-danger' : 'text-muted'}>
            {expired ? 'This code has expired — generate another.' : `Expires in ${formatCountdown(remaining)}`}
          </p>

          {qrError && <p className="text-danger">Couldn’t draw the code: {qrError}</p>}

          <p className="text-muted invite-url">{redeemUrl}</p>

          {publicBaseUrl === null && (
            <p className="text-muted">
              Set <code>Apps:PublicBaseUrl</code> so this points at the install’s canonical address instead of
              whichever one this tab is using — a phone on the Wi-Fi may not be able to reach this one.
            </p>
          )}

          <div className="flex gap-1 mt-2">
            <button className="btn-secondary" disabled={expired} onClick={copyLink}>
              {copied ? 'Copied!' : 'Copy link'}
            </button>
            <button className="btn-secondary" onClick={onClose}>
              Done
            </button>
          </div>
        </div>
      )}
    </Modal>
  );
}

/** "14:03" - a countdown someone is watching while a person types, so it ticks in seconds rather than rounding to minutes. */
function formatCountdown(ms: number): string {
  const total = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`;
}
