/**
 * Which camera, if any, the wall should be showing right now
 * (docs/plans/cameras.md Phase 8). A pure reducer over what
 * /api/motion-events/stream says, kept apart from the EventSource wiring so the
 * rules - which are all about a person standing in front of the tablet, not
 * about SSE - can be tested directly.
 */

/** One frame from the SSE stream. Heartbeats arrive under their own event name and never reach here. */
export interface MotionChange {
  deviceId: string;
  isActive: boolean;
}

export interface MotionState {
  /** Devices currently in motion, most recently started first. */
  active: string[];
  /** The device whose modal was closed by hand, if it is still in motion. */
  dismissed: string | null;
}

export const initialMotionState: MotionState = { active: [], dismissed: null };

/**
 * Folds one change in.
 *
 * The stream carries absolute state rather than toggles, and the API documents
 * that it may repeat a device already reported active - a kiosk connecting
 * mid-motion gets a snapshot and can then be handed a change queued from just
 * before the snapshot was read. So a repeat has to be a no-op here, and in
 * particular must not undo a dismissal: otherwise a modal the user just closed
 * reopens on its own.
 */
export function applyMotionChange(state: MotionState, change: MotionChange): MotionState {
  const wasActive = state.active.includes(change.deviceId);

  if (change.isActive) {
    if (wasActive) return state;

    return {
      // Newest first: when two cameras see something at once, the wall shows
      // whichever moved most recently.
      active: [change.deviceId, ...state.active],
      // Motion *starting* on a device is a new event, so a dismissal of that
      // device's last event no longer applies. Reached only when the device was
      // not already active, which is what makes a repeated frame harmless.
      dismissed: state.dismissed === change.deviceId ? null : state.dismissed,
    };
  }

  if (!wasActive && state.dismissed !== change.deviceId) return state;

  return {
    active: state.active.filter((id) => id !== change.deviceId),
    // The dismissal dies with the event it dismissed. Keeping it would be
    // indistinguishable from the device being muted forever.
    dismissed: state.dismissed === change.deviceId ? null : state.dismissed,
  };
}

/**
 * The X button. Closes the modal for the event now showing, not for the camera:
 * the next time that camera sees something the wall speaks up again.
 *
 * Takes the device it is closing rather than reading the visible one out of
 * this state, because since Phase 12 the visible camera is not always motion's
 * to know - a camera opened from its button on the dashboard is on screen for
 * reasons this reducer never hears about. A device that is not in motion is
 * therefore an ordinary no-op rather than a mistake: closing a hand-opened feed
 * has nothing to dismiss unless that camera happens to be seeing something too,
 * which is exactly the case where it does need dismissing - otherwise the modal
 * would stay open on the same camera, under motion's ownership, and the X would
 * visibly do nothing.
 */
export function applyDismissal(state: MotionState, deviceId: string): MotionState {
  if (!state.active.includes(deviceId)) return state;

  return { ...state, dismissed: deviceId };
}

/**
 * The device to show, or null for no modal. Skips a dismissed device rather
 * than stopping at it, so dismissing the front camera still surfaces the back
 * one if both are seeing something.
 */
export function visibleCameraDeviceId(state: MotionState): string | null {
  return state.active.find((id) => id !== state.dismissed) ?? null;
}

/**
 * Everything goes quiet. Used when the stream drops: the API's own dispatcher
 * clears motion when it loses Home Assistant, on the principle that once we
 * cannot see, we stop claiming there is something to look at - and a modal
 * pinned open by a change we will now never receive is exactly that claim.
 */
export function clearMotion(): MotionState {
  return initialMotionState;
}
