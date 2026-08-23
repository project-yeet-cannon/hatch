import { useEffect, useState } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faXmark } from '@fortawesome/free-solid-svg-icons';
import { useCameraStream } from '../hooks/useCameraStream';
import { clientLogger } from '../lib/clientLogger';

/**
 * The live camera feed, over the dashboard, for as long as something is moving
 * in front of that camera (docs/plans/cameras.md Phase 8).
 *
 * Built on GatherOverlay's shape rather than the admin app's Modal: this is a
 * portrait tablet on a wall, so a full-screen overlay with one 56px touch
 * target is the idiom here, where a centred dialog with a small ✕ is the idiom
 * over there. It is also mounted inside .hf-page for the same reason Gather is
 * - the circadian palette is inline custom properties on that element, and an
 * overlay outside it would resolve none of them.
 *
 * It does not close itself. Motion ending closes it, or the ✕ does; both go
 * through the reducer in lib/motionEvents.ts, so a dismissal lasts for the
 * event rather than for the camera.
 */
export function CameraFeedModal({ deviceId, onClose }: { deviceId: string; onClose: () => void }) {
  const { videoRef, error } = useCameraStream(deviceId);
  const name = useCameraName(deviceId);

  return (
    <div className="hf-camera-overlay">
      <div className="hf-camera-bar">
        <div className="hf-camera-title">{name ?? 'Camera'}</div>
        <button className="hf-camera-close" onClick={onClose} aria-label="Close camera feed">
          <FontAwesomeIcon icon={faXmark} />
        </button>
      </div>
      <div className="hf-camera-stage">
        {error ? (
          <div className="hf-note" role="alert">
            {error}
          </div>
        ) : (
          // autoPlay/muted/playsInline together are what let a video start
          // without a tap; see useCameraStream for why muted is load-bearing
          // rather than a preference.
          <video ref={videoRef} className="hf-camera-video" autoPlay muted playsInline />
        )}
      </div>
    </div>
  );
}

/**
 * The camera's name, or null until it arrives.
 *
 * Fetched separately rather than carried on the motion event, and deliberately
 * not awaited before rendering: the video socket opens on mount either way, so
 * a slow lookup delays a label, never the picture. The whole point of the
 * feature is how fast the feed is up.
 */
function useCameraName(deviceId: string): string | null {
  const [name, setName] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setName(null);

    fetch(`/api/devices/${deviceId}`, { headers: { Accept: 'application/json' } })
      .then((res) => (res.ok ? (res.json() as Promise<{ name?: string }>) : null))
      .then((device) => {
        if (!cancelled && device?.name) setName(device.name);
      })
      .catch((err: unknown) => {
        // A missing label is a cosmetic loss on a modal that is already
        // showing video, so this never reaches the user.
        clientLogger.warn('Camera name lookup failed', {
          deviceId,
          reason: err instanceof Error ? err.message : String(err),
        });
      });

    return () => {
      cancelled = true;
    };
  }, [deviceId]);

  return name;
}
