import { useEffect, useState } from 'react';
import { getCameraConnection } from '../api/client';
import { clientLogger } from '../lib/clientLogger';
import { useCameraStream } from '../lib/useCameraStream';
import { Modal } from './Modal';

/**
 * One camera's live feed, in the admin app, from the device that serves it.
 *
 * The same relay and the same MSE client the kiosk uses
 * (docs/camera-devices-architecture.md) - CameraController streams fragmented
 * MP4 from go2rtc over a same-origin WebSocket, so nothing here knows a
 * camera's address or password. What differs is why someone is looking: the
 * kiosk shows a feed because something moved, this shows it because an operator
 * just typed a host into the form below it and wants to know whether it works.
 *
 * That is what the connection lookup is for. A camera with no address is the
 * normal state right after import, and the relay answers it with a 409 - but a
 * failed WebSocket handshake carries no status code to the browser, so on its
 * own it would surface as the same "unavailable" as a wrong password or a
 * camera that is off. Reading the connection alongside the stream, rather than
 * before it, names that case without delaying the picture by a round-trip.
 */
export function CameraLiveViewModal({
  deviceId,
  deviceName,
  onClose,
}: {
  deviceId: string;
  deviceName: string;
  onClose: () => void;
}) {
  const { videoRef, error } = useCameraStream(deviceId);
  const unconfigured = useUnconfigured(deviceId);

  return (
    <Modal open title={`${deviceName} · live`} onClose={onClose}>
      {unconfigured ? (
        <p className="text-muted">
          This camera has no address yet, so there is nothing to stream. Set its
          host under <strong>Channels → Camera connection</strong>, then reopen
          this.
        </p>
      ) : error ? (
        <p className="text-danger">{error}</p>
      ) : (
        // autoPlay/muted/playsInline together are what let a video start
        // without a click; see useCameraStream for why muted is load-bearing
        // rather than a preference.
        <video ref={videoRef} className="camera-live-video" autoPlay muted playsInline />
      )}
      <p className="text-muted mt-2" style={{ fontSize: 'var(--t-label)' }}>
        Live sub-stream, relayed through Aerie. Closing this releases the
        camera's connection.
      </p>
    </Modal>
  );
}

/**
 * Whether this camera is known to have no address - false until the lookup says
 * otherwise, so a slow or failed lookup leaves the stream to speak for itself
 * rather than accusing a working camera of being unconfigured.
 */
function useUnconfigured(deviceId: string): boolean {
  const [unconfigured, setUnconfigured] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setUnconfigured(false);

    getCameraConnection(deviceId)
      .then((connection) => {
        if (!cancelled) setUnconfigured(!connection.effectiveHost);
      })
      .catch((err: unknown) => {
        clientLogger.warn('Camera connection lookup failed', {
          deviceId,
          reason: err instanceof Error ? err.message : String(err),
        });
      });

    return () => {
      cancelled = true;
    };
  }, [deviceId]);

  return unconfigured;
}
