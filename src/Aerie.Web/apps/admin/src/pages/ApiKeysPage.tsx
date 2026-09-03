import { useEffect, useState } from 'react';
import { Badge, Button, Card, Modal, PageHeader, Table, Text } from '@aerie/ui';
import { createApiKey, getApiKeys, revokeApiKey } from '../api/client';
import { formatAge } from '../lib/format';
import { API_KEY_SCOPES } from '../types';
import type { ApiKey, ApiKeyMinted, ApiKeyScope } from '../types';

/**
 * The credentials that are not browsers - today, Claude working an issue in
 * Hatch through `Authorization: Bearer`.
 *
 * It sits beside Sessions because it is the same question asked about a
 * different kind of holder, and it is a separate page because the two differ in
 * every control: a session is enrolled by reading a code out and revoked by
 * deleting a row, a key is minted into a file and revoked by a column that
 * stays, so that the audit trail naming it goes on resolving.
 *
 * A key carries scopes and reaches exactly what they name. Everything an
 * operator does - including minting the next key - stays closed to one, which
 * is why this page can only be reached by a person.
 */
export function ApiKeysPage() {
  const [keys, setKeys] = useState<ApiKey[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [scopes, setScopes] = useState<ApiKeyScope[]>(['hatch']);
  const [minting, setMinting] = useState(false);
  const [minted, setMinted] = useState<ApiKeyMinted | null>(null);

  useEffect(() => {
    void load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setKeys(await getApiKeys());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function mint() {
    setMinting(true);
    setError(null);
    try {
      setMinted(await createApiKey(name.trim(), scopes));
      setName('');
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setMinting(false);
    }
  }

  async function revoke(key: ApiKey) {
    if (!confirm(`Revoke "${key.name}"? Whatever is holding it stops working on its next request, for good.`)) return;

    setError(null);
    try {
      await revokeApiKey(key.id);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  function toggleScope(scope: ApiKeyScope, wanted: boolean) {
    setScopes((prev) => (wanted ? [...new Set([...prev, scope])] : prev.filter((s) => s !== scope)));
  }

  return (
    <div>
      <PageHeader
        title="API keys"
        actions={
          <>
            <input
              type="text"
              placeholder="What is holding it, e.g. Claude"
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
            {/* Checkboxes rather than a select, because scopes are a set and
                because the list is short enough to read at a glance. A key
                with none reaches nothing, which is the right way for a
                half-filled form to fail. */}
            {API_KEY_SCOPES.map((scope) => (
              <label key={scope} className="flex gap-1" style={{ alignItems: 'center' }}>
                <input
                  type="checkbox"
                  checked={scopes.includes(scope)}
                  onChange={(e) => toggleScope(scope, e.target.checked)}
                />
                {scope}
              </label>
            ))}
            <Button variant="primary" disabled={minting || name.trim() === ''} onClick={() => void mint()}>
              {minting ? 'Minting…' : 'Mint key'}
            </Button>
            <Button onClick={() => void load()}>Refresh</Button>
          </>
        }
      />

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {loading && <Text tone="muted">Loading…</Text>}

      {!loading && keys.length === 0 && (
        <Card>
          <p>
            No keys. <strong>Mint key</strong> makes one, shows it once, and never again — it belongs in a file
            outside this repo, not in it.
          </p>
        </Card>
      )}

      {!loading && keys.length > 0 && (
        <Card>
          <Table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Key</th>
                <th>Scopes</th>
                <th>Minted</th>
                <th>Last used</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {keys.map((key) => (
                <tr key={key.id}>
                  <td>
                    <div className="flex gap-1" style={{ alignItems: 'center' }}>
                      {key.name}
                      {key.revokedAt && <Badge tone="danger">Revoked</Badge>}
                    </div>
                  </td>
                  {/* The prefix is what matches a row against the value in a
                      config file. It is the only part of the secret that was
                      ever stored, and it cannot be used for anything. */}
                  <td>
                    <code>{key.prefix}…</code>
                  </td>
                  <td>{key.scopes.length > 0 ? key.scopes.join(', ') : <Text tone="muted">None</Text>}</td>
                  <td>{formatAge(key.createdAt)}</td>
                  <td>{key.lastUsedAt ? formatAge(key.lastUsedAt) : 'Never'}</td>
                  <td>
                    {key.revokedAt ? (
                      <Text tone="muted">{formatAge(key.revokedAt)}</Text>
                    ) : (
                      <Button variant="danger" onClick={() => void revoke(key)}>
                        Revoke
                      </Button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Card>
      )}

      <MintedModal minted={minted} onClose={() => setMinted(null)} />
    </div>
  );
}

/**
 * The secret, once. It is stored only as a hash, so closing this is the end of
 * it - a lost key is replaced by minting another, never by looking this one up.
 */
function MintedModal({ minted, onClose }: { minted: ApiKeyMinted | null; onClose: () => void }) {
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    setCopied(false);
  }, [minted]);

  async function copy() {
    if (!minted) return;
    await navigator.clipboard.writeText(minted.secret);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  }

  return (
    <Modal open={minted !== null} onClose={onClose} title="Copy this now">
      {minted && (
        <div className="flex flex-col gap-2">
          <Text tone="muted">
            This is the only time “{minted.key.name}” will be shown. Put it in a file outside the repo and hand
            that path to whatever is going to use it.
          </Text>

          <code style={{ wordBreak: 'break-all' }}>{minted.secret}</code>

          <div className="flex gap-1">
            <Button variant="primary" onClick={() => void copy()}>
              {copied ? 'Copied' : 'Copy'}
            </Button>
            <Button onClick={onClose}>Done</Button>
          </div>
        </div>
      )}
    </Modal>
  );
}
