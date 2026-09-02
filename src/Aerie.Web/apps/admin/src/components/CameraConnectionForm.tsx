import { Button, Field, Grid, Text } from '@aerie/ui';
import { useEffect, useState } from "react";
import { getCameraConnection, saveCameraConnection } from "../api/client";
import type { CameraConnection } from "../types";

/**
 * How to reach a camera's stream, as a form (docs/camera-devices-architecture.md).
 * Before this, a camera's address and password lived in a GitHub secret that
 * reached the cluster through a workflow dispatch and a pod restart; adding a
 * camera is this panel now.
 *
 * The password box is the part with a rule in it. The API will never hand back
 * a stored password, so the box cannot be pre-filled - which means an empty box
 * is ambiguous unless something else resolves it. `hasPassword` is that
 * something: the placeholder says whether one is set, and leaving the box
 * untouched sends `undefined`, which the API reads as "leave it alone". Without
 * that, every save that only changed the host would silently clear the
 * credential.
 */
export function CameraConnectionForm({ deviceId }: { deviceId: string }) {
  const [connection, setConnection] = useState<CameraConnection | null>(null);
  const [host, setHost] = useState("");
  const [port, setPort] = useState("554");
  const [streamPath, setStreamPath] = useState("");
  const [username, setUsername] = useState("");
  // null means "not edited in this session" - distinct from "" which clears.
  const [password, setPassword] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    getCameraConnection(deviceId)
      .then((c) => {
        if (cancelled) return;
        apply(c);
        setError(null);
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof Error ? err.message : String(err));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [deviceId]);

  function apply(c: CameraConnection) {
    setConnection(c);
    setHost(c.host ?? "");
    setPort(String(c.port));
    setStreamPath(c.streamPath);
    setUsername(c.username ?? "");
    setPassword(null);
  }

  async function save() {
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      const updated = await saveCameraConnection(deviceId, {
        host: host.trim() === "" ? null : host.trim(),
        port: Number(port) || null,
        streamPath: streamPath.trim() === "" ? null : streamPath.trim(),
        username: username.trim() === "" ? null : username.trim(),
        // Omitted entirely when untouched, which is what keeps a save that only
        // changed the host from wiping the password.
        ...(password === null ? {} : { password }),
      });
      apply(updated);
      setSaved(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  if (loading) return <Text tone="muted">Loading camera connection…</Text>;

  const hostHint = connection?.discoveredHost
    ? host.trim() === ""
      ? `Using ${connection.discoveredHost}, from Home Assistant — it follows the camera if its address changes.`
      : `Overriding Home Assistant, which reports ${connection.discoveredHost}. Clear this box to go back to that.`
    : "Home Assistant did not report an address for this device, so it has to be set here.";

  const passwordPlaceholder = connection?.hasPassword
    ? "•••••••• (unchanged)"
    : "No password set";

  return (
    <div className="mt-2">
      <h4 className="mb-1">Camera connection</h4>
      <Text tone="muted" className="mb-2">
        Where this camera's video comes from. Aerie hands these to the stream
        server when someone opens the feed — they are not stored in the cluster.
      </Text>

      <Grid cols={3}>
        <Field label="Host" hint={hostHint}>
          <input
            type="text"
            value={host}
            placeholder={connection?.discoveredHost ?? "e.g. 10.0.0.9"}
            onChange={(e) => setHost(e.target.value)}
          />
        </Field>

        <Field label="RTSP port">
          <input
            type="number"
            value={port}
            onChange={(e) => setPort(e.target.value)}
          />
        </Field>

        <Field label="Stream path">
          <input
            type="text"
            value={streamPath}
            onChange={(e) => setStreamPath(e.target.value)}
          />
        </Field>

        <Field label="Username">
          <input
            type="text"
            value={username}
            autoComplete="off"
            onChange={(e) => setUsername(e.target.value)}
          />
        </Field>

        <Field
          label="Password"
          hint={
            password === '' && connection?.hasPassword ? 'Saving now clears the stored password.' : undefined
          }
        >
          <input
            type="password"
            value={password ?? ""}
            placeholder={passwordPlaceholder}
            autoComplete="new-password"
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>
      </Grid>

      <div className="flex gap-1 mt-2" style={{ alignItems: "center" }}>
        <Button variant="primary" onClick={save} disabled={saving}>
          {saving ? "Saving…" : "Save connection"}
        </Button>
        {saved && <Text as="span" tone="success">Saved.</Text>}
        {error && <Text as="span" tone="danger">{error}</Text>}
      </div>

      {connection?.effectiveHost && (
        <Text tone="muted" className="mt-2" style={{ fontSize: 'var(--t-label)' }}>
          Stream source: rtsp://
          {connection.username ? `${connection.username}:••••@` : ""}
          {connection.effectiveHost}:{connection.port}
          {connection.streamPath.startsWith("/") ? "" : "/"}
          {connection.streamPath}
        </Text>
      )}
    </div>
  );
}
