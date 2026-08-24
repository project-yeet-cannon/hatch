import { useCallback, useEffect, useState } from 'react';
import { clientLogger } from '../lib/clientLogger';
import {
  applyDismissal,
  applyMotionChange,
  clearMotion,
  initialMotionState,
  visibleCameraDeviceId,
  type MotionChange,
} from '../lib/motionEvents';

/** The API's SSE endpoint - see MotionEventsController. */
const MOTION_STREAM_URL = '/api/motion-events/stream';

/**
 * Subscribes to the motion stream for the life of the app and reports which
 * camera the wall should be showing (docs/plans/cameras.md Phase 8). The rules
 * live in lib/motionEvents.ts; this is the wiring.
 *
 * EventSource rather than a WebSocket or a poll, because it reconnects on its
 * own. A kiosk is a tablet on a wall that nobody logs into: an API restart, a
 * Wi-Fi blip or a rolling deploy has to heal without anyone touching it, and
 * that is the entire client-side cost of it doing so.
 */
export function useMotionEvents(): { cameraDeviceId: string | null; dismiss: (deviceId: string) => void } {
  const [state, setState] = useState(initialMotionState);

  useEffect(() => {
    clientLogger.info('Subscribing to motion events');
    const source = new EventSource(MOTION_STREAM_URL);

    source.onmessage = (event: MessageEvent<string>) => {
      let change: MotionChange;
      try {
        change = JSON.parse(event.data) as MotionChange;
      } catch {
        clientLogger.warn('Unparseable motion event frame');
        return;
      }
      if (typeof change?.deviceId !== 'string' || typeof change?.isActive !== 'boolean') return;

      clientLogger.info('Motion event', { deviceId: change.deviceId, isActive: change.isActive });
      setState((current) => applyMotionChange(current, change));
    };

    // Fired on every drop, including the ones EventSource then reconnects from
    // on its own - so this is not a place to tear anything down, only to stop
    // claiming motion we can no longer see. The reconnect replays a snapshot of
    // whatever is still active, which is what restores the modal if the motion
    // outlived the outage.
    source.onerror = () => {
      clientLogger.warn('Motion event stream dropped; EventSource will retry');
      setState(clearMotion);
    };

    return () => {
      source.close();
    };
  }, []);

  // Takes the device rather than assuming it is the one this hook would show:
  // since Phase 12 the modal on screen may be one someone opened from its
  // button, and closing that must not dismiss a *different* camera's motion
  // event. See applyDismissal.
  const dismiss = useCallback((deviceId: string) => {
    setState((current) => applyDismissal(current, deviceId));
  }, []);

  return { cameraDeviceId: visibleCameraDeviceId(state), dismiss };
}
