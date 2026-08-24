import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faVideo } from '@fortawesome/free-solid-svg-icons';
import type { CameraSummary } from '../types';
import { clientLogger } from '../lib/clientLogger';

/**
 * A tap-to-watch button per camera, in its own row directly below the routines
 * (docs/plans/cameras.md Phase 12). Same tile as a routine's - literally, the
 * geometry rules in theme.css are shared - because it belongs to the same band
 * of the screen and the same gesture, while sitting in its own row because
 * tapping it does something categorically different from triggering a routine.
 *
 * Nothing here talks to the network. Tapping hands the device id upwards and
 * App opens the same CameraFeedModal a motion event would, which is the whole
 * unification: one way a camera looks on this wall, whether someone asked for
 * it or something walked in front of it.
 *
 * Every enabled camera gets a button, including one nobody has finished
 * configuring in the admin UI - the modal says so when you tap it. A camera
 * that is simply absent from the wall looks identical to one that was never
 * imported, and nobody standing in the hall can tell those apart.
 */
export function CamerasSection({ cameras, onOpen }: { cameras: CameraSummary[]; onOpen: (deviceId: string) => void }) {
  return (
    <div className="hf-cameras">
      {cameras.map((camera) => (
        <button
          key={camera.id}
          type="button"
          className="hf-camera-tile"
          onClick={() => {
            clientLogger.info('Camera feed opened by hand', { deviceId: camera.id, name: camera.name });
            onOpen(camera.id);
          }}
        >
          <span className="hf-camera-tile-circle">
            <FontAwesomeIcon icon={faVideo} className="hf-camera-tile-icon" />
          </span>
          <span className="hf-camera-tile-name">{camera.name}</span>
        </button>
      ))}
    </div>
  );
}
