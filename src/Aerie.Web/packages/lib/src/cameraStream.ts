/**
 * The rules half of the camera feed (docs/camera-devices-architecture.md),
 * separated from the modal that plays it so the parts with decisions in them
 * can be tested without a MediaSource, a WebSocket or a camera - the same split
 * MotionEventStream/MotionEventsController makes on the API side.
 *
 * Shared rather than copied. Admin and the dashboard both relay this protocol
 * from the same endpoint, and until now each held its own copy of this file:
 * same name, contents already drifted, and nothing in the build that would ever
 * have said so. Both now import it from here, and the tests below it are the
 * only tests it has ever had.
 *
 * What is being spoken here is go2rtc's stream protocol, relayed byte-for-byte
 * by Aerie.Api's CameraController: a short JSON control exchange, then binary
 * fragmented MP4 forever. Verified against go2rtc 1.9.14 on 2026-08-23 -
 *
 *   -> {"type":"mse","value":"avc1.640028,avc1.4d002a,..."}   codecs we can play
 *   <- {"type":"mse","value":"video/mp4; codecs=\"avc1.640029\""}
 *   <- <binary ftyp+moov>                                     init segment
 *   <- <binary moof+mdat> ...                                 fragments
 *
 * or, when it can't serve the stream:
 *
 *   <- {"type":"error","value":"mse: stream not found"}
 */

/**
 * Codecs to offer go2rtc, in the order they are worth having. All H.264,
 * because the plan deliberately puts the kiosk on a Reolink *sub*-stream: the
 * main stream on the 4K/12MP models is H.265, which no browser here decodes,
 * and asking for it would mean a transcode on a cluster node for every viewer.
 *
 * Profiles rather than a single string because a camera's SPS decides the
 * profile and level, not us - baseline through high, and 4d002a/640029 are the
 * main/high entries a 1080p30 stream lands on. Each is checked against the
 * browser before being offered (see supportedCodecs).
 */
const CANDIDATE_CODECS = [
  'avc1.640029', // High @ 4.1
  'avc1.640028', // High @ 4.0
  'avc1.4d002a', // Main @ 4.2
  'avc1.4d0029', // Main @ 4.1
  'avc1.42e01e', // Baseline @ 3.0
];

/** How far behind the newest buffered frame the video may drift before it is nudged forward. */
export const MAX_DRIFT_SECONDS = 1;

/** Where the nudge lands - just behind the live edge, not exactly on it, since seeking to the very end stalls playback waiting for a frame that has not arrived. */
export const LIVE_EDGE_MARGIN_SECONDS = 0.3;

/**
 * How much history to keep in the SourceBuffer. A MediaSource has a finite
 * quota and a kiosk modal can stay open for the length of a motion event, so
 * without trimming an append eventually throws QuotaExceededError and the feed
 * dies - the failure this exists to prevent. Nobody scrubs backwards on a wall
 * display, so the only reason to keep any history at all is that removing right
 * up to the playhead would cut off the frame being decoded.
 */
export const MAX_BUFFER_SECONDS = 30;

/**
 * The codecs this browser can actually play, as the comma-separated string
 * go2rtc expects. Empty when MediaSource is missing or supports none of them,
 * which the caller reports rather than opening a socket that cannot succeed.
 */
export function supportedCodecs(
  isTypeSupported: (type: string) => boolean = (type) =>
    typeof MediaSource !== 'undefined' && MediaSource.isTypeSupported(type),
): string {
  return CANDIDATE_CODECS.filter((codec) => isTypeSupported(`video/mp4; codecs="${codec}"`)).join(',');
}

/** The request that opens the video half of the protocol, once the socket is up. */
export function mseRequest(codecs: string): string {
  return JSON.stringify({ type: 'mse', value: codecs });
}

export type ControlMessage =
  /** go2rtc accepted; `mimeType` is the exact string to hand addSourceBuffer. */
  | { kind: 'ready'; mimeType: string }
  /** go2rtc refused - an unknown stream name, a codec it cannot produce, a camera it cannot reach. */
  | { kind: 'error'; message: string }
  /** Anything else go2rtc says. Not an error: it sends other message types this client has no use for. */
  | { kind: 'ignored' };

/**
 * Reads one text frame from go2rtc.
 *
 * The MIME string in a `ready` reply is used verbatim and never reconstructed
 * from what was requested - go2rtc answers with the codec the *stream* actually
 * carries, which need not be one of the offered strings. Asking for
 * avc1.640028 and being handed avc1.640029 is the observed normal case, and
 * passing the requested string to addSourceBuffer instead would reject every
 * fragment that followed.
 */
export function parseControlMessage(raw: string): ControlMessage {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { kind: 'ignored' };
  }

  if (typeof parsed !== 'object' || parsed === null) return { kind: 'ignored' };
  const { type, value } = parsed as { type?: unknown; value?: unknown };

  if (type === 'error') {
    return { kind: 'error', message: typeof value === 'string' && value ? value : 'Camera stream refused' };
  }
  if (type === 'mse' && typeof value === 'string' && value) {
    return { kind: 'ready', mimeType: value };
  }
  return { kind: 'ignored' };
}

/**
 * How far to jump the playhead forward, or null to leave it alone.
 *
 * Live video over MSE drifts: the browser decodes at its own pace while
 * fragments keep arriving, and a tab that was backgrounded (a kiosk between
 * motion events) comes back seconds behind. Left alone the modal shows footage
 * of what already happened, which is the opposite of the point.
 */
export function driftCorrection(currentTime: number, bufferedEnd: number): number | null {
  if (!Number.isFinite(bufferedEnd) || bufferedEnd <= 0) return null;
  if (bufferedEnd - currentTime <= MAX_DRIFT_SECONDS) return null;

  const target = bufferedEnd - LIVE_EDGE_MARGIN_SECONDS;

  // Never move backwards: a currentTime already past the buffered end happens
  // briefly around a seek, and correcting it would stall playback.
  return target > currentTime ? target : null;
}

/**
 * The range to evict from the SourceBuffer, or null when there is nothing worth
 * removing. Keeps MAX_BUFFER_SECONDS behind the playhead.
 */
export function trimRange(
  currentTime: number,
  bufferedStart: number,
): { start: number; end: number } | null {
  const cutoff = currentTime - MAX_BUFFER_SECONDS;
  if (!Number.isFinite(bufferedStart) || cutoff <= bufferedStart) return null;

  return { start: bufferedStart, end: cutoff };
}
