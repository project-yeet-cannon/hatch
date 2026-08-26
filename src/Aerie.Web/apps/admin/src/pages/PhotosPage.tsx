import { useEffect, useState } from 'react';
import {
  getPhotoAlbums,
  getPhotoCarousel,
  getPhotosStatus,
  getSettings,
  photoAlbumCoverUrl,
  photoAssetUrl,
  putSetting,
  refreshPhotoAlbums,
  updatePhotoAlbum,
} from '../api/client';
import type { PhotoAlbum, PhotoCarousel, PhotosStatus } from '../types';

/**
 * Where the house's photo library is, and which of its albums the kiosk may
 * show.
 *
 * The credentials are ordinary site settings written with putSetting, exactly
 * as the Home Assistant token and the Google client secret are — the key goes
 * through SecretProtector on the way in and comes back redacted, so this page
 * can say "a key is set" without ever holding one. There is deliberately no
 * second credential store for Immich.
 *
 * Every image on this page is proxied by Aerie. Nothing here ever learns the
 * Immich host well enough to fetch from it directly, which is the same property
 * the kiosk relies on (docs/plans/immich.md v+2).
 */
export function PhotosPage() {
  const [status, setStatus] = useState<PhotosStatus | null>(null);
  const [albums, setAlbums] = useState<PhotoAlbum[]>([]);
  const [preview, setPreview] = useState<PhotoCarousel | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const [nextStatus, nextAlbums] = await Promise.all([getPhotosStatus(), getPhotoAlbums()]);
      setStatus(nextStatus);
      setAlbums(nextAlbums);
      // Only worth asking once something is included: an empty carousel below
      // an empty selection is furniture.
      setPreview(nextStatus.includedAlbumCount > 0 ? await getPhotoCarousel(PREVIEW_SIZE) : null);
    } catch (err) {
      setError(messageOf(err));
    } finally {
      setLoading(false);
    }
  }

  /** Re-reads the album list from Immich. What you press after making an album over there. */
  async function refresh() {
    setRefreshing(true);
    setError(null);
    setNotice(null);
    try {
      const result = await refreshPhotoAlbums();
      setNotice(`${result.added} added, ${result.updated} updated, ${result.removed} removed.`);
      await load();
    } catch (err) {
      setError(messageOf(err));
    } finally {
      setRefreshing(false);
    }
  }

  /** Optimistic, then reconciled against what the server actually stored — RoutinesPage and CalendarsPage's shape. */
  async function setIncluded(album: PhotoAlbum, included: boolean) {
    setAlbums((prev) => prev.map((a) => (a.id === album.id ? { ...a, included } : a)));
    setError(null);
    setNotice(null);
    try {
      const saved = await updatePhotoAlbum(album.id, { included, sortOrder: null });
      setAlbums((prev) => prev.map((a) => (a.id === saved.id ? saved : a)));
      // The wall's view of the selection just changed, and this card is the
      // only place anyone can check that it did.
      setPreview(await getPhotoCarousel(PREVIEW_SIZE));
    } catch (err) {
      setError(messageOf(err));
      await load();
    }
  }

  const includedCount = albums.filter((a) => a.included).length;

  return (
    <div>
      <div className="admin-page-header">
        <h2>Photos</h2>
        <div className="flex gap-1" style={{ alignItems: 'center' }}>
          <button className="btn-secondary" disabled={refreshing || !status?.isConfigured} onClick={refresh}>
            {refreshing ? 'Refreshing…' : 'Refresh albums'}
          </button>
          <button className="btn-secondary" onClick={load}>
            Reload
          </button>
        </div>
      </div>

      {error && <p className="text-danger mb-2">{error}</p>}
      {notice && <p className="text-muted mb-2">{notice}</p>}

      <ConnectionCard status={status} onSaved={load} />

      {loading && <p className="text-muted">Loading…</p>}

      {!loading && status?.isConfigured && (
        <div className="card mb-2">
          <div className="flex between" style={{ alignItems: 'flex-start' }}>
            <div>
              <h3>Albums</h3>
              <p className="text-muted mt-1 mb-1">
                {albums.length === 0
                  ? 'None discovered yet.'
                  : `${includedCount} of ${albums.length} on the kiosk carousel.`}{' '}
                Everything is off until you tick it — connecting a library must not put every photo in it on a kitchen
                wall.
              </p>
            </div>
          </div>

          {albums.length === 0 ? (
            <p className="text-muted">
              <strong>Refresh albums</strong> asks Immich what it has. An album made over there shows up here after
              that, not before.
            </p>
          ) : (
            <table className="admin-table">
              <thead>
                <tr>
                  <th style={{ width: '1%' }}>Show</th>
                  <th style={{ width: '1%' }}>Cover</th>
                  <th>Album</th>
                  <th style={{ width: '1%' }}>Photos</th>
                </tr>
              </thead>
              <tbody>
                {albums.map((album) => (
                  <tr key={album.id}>
                    <td>
                      <input
                        type="checkbox"
                        aria-label={`Show ${album.name} on the kiosk`}
                        checked={album.included}
                        onChange={(e) => setIncluded(album, e.target.checked)}
                      />
                    </td>
                    <td>
                      {album.hasCover ? (
                        <img
                          className="photo-cover"
                          src={photoAlbumCoverUrl(album.id)}
                          alt=""
                          loading="lazy"
                        />
                      ) : (
                        <div className="photo-cover photo-cover-empty" aria-hidden />
                      )}
                    </td>
                    <td>
                      <strong>{album.name}</strong>
                      {album.description && <p className="text-muted mt-1 mb-1">{album.description}</p>}
                    </td>
                    <td>{album.assetCount.toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {!loading && status?.isConfigured && <PreviewCard preview={preview} />}

      <SetupNote />
    </div>
  );
}

/** How many photos the preview strip asks for. Enough to prove the selection reached the wall, not enough to be a gallery. */
const PREVIEW_SIZE = 12;

/**
 * The host and the key, plus a live answer about whether they work. Both are
 * written one at a time, so a mistyped key doesn't also lose the host.
 */
function ConnectionCard({ status, onSaved }: { status: PhotosStatus | null; onSaved: () => Promise<void> }) {
  const [host, setHost] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [keyIsSet, setKeyIsSet] = useState(false);
  const [saving, setSaving] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);

  useEffect(() => {
    // Straight from the settings endpoint rather than from the status DTO: the
    // host is an editable field here, and the value in the box has to be the
    // stored one. The key never arrives — it comes back redacted, which is all
    // this needs to know.
    getSettings().then(
      (settings) => {
        setHost(settings.find((s) => s.key === 'ImmichBaseUrl')?.value ?? '');
        setKeyIsSet((settings.find((s) => s.key === 'ImmichApiKey')?.value.length ?? 0) > 0);
      },
      (err: unknown) => setSaveError(messageOf(err)),
    );
  }, []);

  async function save(key: string, value: string) {
    setSaving(key);
    setSaveError(null);
    try {
      await putSetting(key, value);
      if (key === 'ImmichApiKey') {
        setKeyIsSet(true);
        // Not held after it is stored. The field goes back to "a key is set".
        setApiKey('');
      }
      await onSaved();
    } catch (err) {
      setSaveError(messageOf(err));
    } finally {
      setSaving(null);
    }
  }

  return (
    <div className="card mb-2">
      <h3>Immich connection</h3>
      <p className="text-muted mt-1 mb-2">
        Aerie talks to Immich; the tablets never do. Every photo on the wall is fetched by the API and passed through,
        so the key below stays on the server and no kiosk needs a second sign-in.
      </p>

      <div className="grid cols-2">
        <div className="field">
          <label className="field-label" htmlFor="immich-host">
            Immich host
          </label>
          <div className="flex gap-1">
            <input
              id="immich-host"
              type="text"
              value={host}
              placeholder="https://photos.example.com"
              onChange={(e) => setHost(e.target.value)}
            />
            <button className="btn-secondary" disabled={saving === 'ImmichBaseUrl'} onClick={() => save('ImmichBaseUrl', host)}>
              {saving === 'ImmichBaseUrl' ? 'Saving…' : 'Save'}
            </button>
          </div>
          <p className="text-muted mt-1">
            Scheme and host, no trailing <code>/api</code>. It has to be reachable from the API pods, not from your
            laptop — on this install that is the LAN or the tailnet.
          </p>
        </div>

        <div className="field">
          <label className="field-label" htmlFor="immich-key">
            Immich API key
          </label>
          <div className="flex gap-1">
            <input
              id="immich-key"
              type="password"
              value={apiKey}
              placeholder={keyIsSet ? 'Key is set — enter a new value to change it' : undefined}
              onChange={(e) => setApiKey(e.target.value)}
            />
            <button className="btn-secondary" disabled={saving === 'ImmichApiKey' || !apiKey} onClick={() => save('ImmichApiKey', apiKey)}>
              {saving === 'ImmichApiKey' ? 'Saving…' : 'Save'}
            </button>
          </div>
          <p className="text-muted mt-1">
            From Immich: <strong>Account settings → API Keys → New API Key</strong>. Tick <code>album.read</code> and{' '}
            <code>asset.view</code> — that is everything Aerie does here, and nothing on that list lets it write.
            Optionally <code>server.about</code>, which only adds the version to the line below. Stored obfuscated and
            never sent back to a browser; leave blank to keep the current one.
          </p>
        </div>
      </div>

      {saveError && <p className="text-danger mt-1">{saveError}</p>}

      <p className="mt-2 mb-1">
        <ConnectionState status={status} />
      </p>
    </div>
  );
}

/** One sentence about whether this works, in the terms an operator can act on. */
function ConnectionState({ status }: { status: PhotosStatus | null }) {
  if (status === null) return <span className="text-muted">Checking…</span>;

  if (!status.isConfigured) {
    return (
      <span className="text-muted">
        {status.baseUrl ? 'Host set, no API key yet.' : 'No host set yet.'} Photos stays off the kiosk until both are
        in.
      </span>
    );
  }

  if (status.reachable) {
    return (
      <span className="text-success">
        Connected to {status.baseUrl}
        {status.version ? ` (Immich ${status.version})` : ''} · {status.albumCount} album
        {status.albumCount === 1 ? '' : 's'}, {status.includedAlbumCount} on the kiosk.
      </span>
    );
  }

  return <span className="text-danger">{explain(status.error)}</span>;
}

/** ImmichClient's error codes, in words. Anything unrecognized is shown as-is rather than swallowed. */
function explain(error: string | null): string {
  switch (error) {
    case 'unauthorized':
      return 'Immich refused that API key. Check it was copied whole, that it has not been revoked over there, and that it grants album.read and asset.view.';
    case 'unreachable':
      return 'Nothing answered at that host. Check the address, and that the API can reach it — it is on the house network, not the internet.';
    case 'not_found':
      return 'That host answered, but not as an Immich server. Check the address does not include a path.';
    case null:
      return 'Immich did not answer.';
    default:
      return `Immich answered with an error: ${error}`;
  }
}

/**
 * What the kiosk would draw right now. Config verification, and the only place
 * an operator can see the selection resolved into actual photos before walking
 * to the kitchen to look at the tablet.
 */
function PreviewCard({ preview }: { preview: PhotoCarousel | null }) {
  if (preview === null) return null;

  return (
    <div className="card mb-2">
      <h3>On the kiosk</h3>
      {preview.error && (
        <p className="text-danger mt-1 mb-1">
          The library could not be fully refreshed ({explain(preview.error)}) — what is below may be out of date.
        </p>
      )}
      <p className="text-muted mt-1 mb-2">
        {preview.totalPhotos.toLocaleString()} photo{preview.totalPhotos === 1 ? '' : 's'} in the selection. The
        carousel shuffles, so no two tablets are on the same one.
      </p>
      {preview.photos.length === 0 ? (
        <p className="text-muted">Nothing to show yet — tick an album above.</p>
      ) : (
        <div className="photo-strip">
          {preview.photos.map((photo) => (
            <img
              key={photo.assetId}
              className="photo-strip-item"
              src={photoAssetUrl(photo.assetId, 'thumbnail')}
              alt={photo.albumName}
              title={photo.albumName}
              loading="lazy"
            />
          ))}
        </div>
      )}
    </div>
  );
}

function SetupNote() {
  return (
    <div className="card">
      <h3>Setting this up</h3>
      <ol className="text-muted">
        <li>
          In Immich, open Account settings → API Keys and make one. Grant it <code>album.read</code> and{' '}
          <code>asset.view</code>; add <code>server.about</code> if you want the version reported here. Nothing else —
          Aerie only reads. Copy the key once; Immich will not show it again.
        </li>
        <li>Paste the host and the key above, saving each.</li>
        <li>
          <strong>Refresh albums</strong>, then tick the albums the family would want on a kitchen wall. Every album is
          off until you say otherwise.
        </li>
      </ol>
      <p className="text-muted">
        Aerie reads Immich and never writes to it. Removing an album here takes it off the wall and changes nothing in
        the library.
      </p>
    </div>
  );
}

function messageOf(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}
