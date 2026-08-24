import { describe, expect, it } from 'vitest';
import {
  applyDismissal,
  applyMotionChange,
  clearMotion,
  initialMotionState,
  visibleCameraDeviceId,
  type MotionChange,
  type MotionState,
} from './motionEvents';

const FRONT = 'front-door-device-id';
const BACK = 'back-yard-device-id';

/** Folds a sequence of frames the way the EventSource handler does. */
function fold(...changes: MotionChange[]): MotionState {
  return changes.reduce(applyMotionChange, initialMotionState);
}

const started = (deviceId: string): MotionChange => ({ deviceId, isActive: true });
const ended = (deviceId: string): MotionChange => ({ deviceId, isActive: false });

describe('applyMotionChange', () => {
  it('shows a camera that just saw something', () => {
    expect(visibleCameraDeviceId(fold(started(FRONT)))).toBe(FRONT);
  });

  it('shows nothing once the motion ends', () => {
    expect(visibleCameraDeviceId(fold(started(FRONT), ended(FRONT)))).toBeNull();
  });

  it('shows the most recent of two cameras in motion at once', () => {
    expect(visibleCameraDeviceId(fold(started(FRONT), started(BACK)))).toBe(BACK);
  });

  it('falls back to the other camera when the newer one goes quiet', () => {
    expect(visibleCameraDeviceId(fold(started(FRONT), started(BACK), ended(BACK)))).toBe(FRONT);
  });

  // The API's documented duplicate: a kiosk connecting mid-motion reads a
  // snapshot and may then be handed a change queued from just before the read.
  it('treats a repeated start as a no-op rather than reordering', () => {
    const state = fold(started(FRONT), started(BACK), started(BACK));

    expect(state.active).toEqual([BACK, FRONT]);
  });

  it('ignores an end for a device that was never active', () => {
    const state = fold(started(FRONT), ended(BACK));

    expect(state.active).toEqual([FRONT]);
  });

  it('returns the same object when nothing changed, so React skips the re-render', () => {
    const state = fold(started(FRONT));

    expect(applyMotionChange(state, started(FRONT))).toBe(state);
    expect(applyMotionChange(state, ended(BACK))).toBe(state);
  });
});

describe('applyDismissal', () => {
  it('closes the modal for the event now showing', () => {
    const state = applyDismissal(fold(started(FRONT)), FRONT);

    expect(visibleCameraDeviceId(state)).toBeNull();
  });

  // The whole reason dismissal is per-event and not per-camera: a wall that
  // went silent about the front door forever would be worse than no feature.
  it('speaks up again the next time that camera sees something', () => {
    const dismissed = applyDismissal(fold(started(FRONT)), FRONT);
    const laterEvent = [ended(FRONT), started(FRONT)].reduce(applyMotionChange, dismissed);

    expect(visibleCameraDeviceId(laterEvent)).toBe(FRONT);
  });

  // The failure this guards: without it, a repeated frame for a device already
  // active would clear the dismissal and reopen a modal the user just closed.
  it('is not undone by a repeated start for the same still-active device', () => {
    const dismissed = applyDismissal(fold(started(FRONT)), FRONT);

    expect(visibleCameraDeviceId(applyMotionChange(dismissed, started(FRONT)))).toBeNull();
  });

  it('still shows another camera that is also in motion', () => {
    const dismissed = applyDismissal(fold(started(FRONT), started(BACK)), BACK);

    expect(visibleCameraDeviceId(dismissed)).toBe(FRONT);
  });

  it('does nothing when no modal is showing', () => {
    expect(applyDismissal(initialMotionState, FRONT)).toBe(initialMotionState);
  });

  // Closing a camera opened from its dashboard button, on a
  // camera that is not in motion. Nothing to dismiss, and in particular nothing
  // else may be dismissed in its place.
  it('leaves another camera showing when handed a device that is not in motion', () => {
    const state = applyDismissal(fold(started(BACK)), FRONT);

    expect(visibleCameraDeviceId(state)).toBe(BACK);
  });
});

describe('clearMotion', () => {
  // Matches what the API's dispatcher does when it loses Home Assistant: once
  // nobody can see, nothing claims there is something to look at. Without it a
  // modal is pinned open by an end-of-motion frame that will never arrive.
  it('closes everything when the stream drops', () => {
    const busy = applyDismissal(fold(started(FRONT), started(BACK)), BACK);

    expect(clearMotion()).toEqual(initialMotionState);
    expect(visibleCameraDeviceId(clearMotion())).toBeNull();
    expect(visibleCameraDeviceId(busy)).not.toBeNull();
  });
});
