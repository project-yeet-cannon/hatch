import { useEffect, useRef, useState } from 'react';
import { clientLogger } from '../lib/clientLogger';
import {
  driftCorrection,
  mseRequest,
  parseControlMessage,
  supportedCodecs,
  trimRange,
} from '../lib/cameraStream';

/**
 * Plays one camera's live video into a &lt;video&gt; element
 * (docs/camera-devices-architecture.md), by way of Aerie.Api's CameraController relay.
 *
 * Media Source Extensions rather than an npm player. The relay hands over
 * fragmented MP4 - which is what MediaSource takes - so the whole client is the
 * wiring below and no dependency at all. HLS was the alternative and would have
 * cost hls.js plus seconds of latency, on a modal whose entire job is to show
 * what is happening at the door right now.
 *
 * The decisions live in lib/cameraStream.ts and are tested there; this is the
 * imperative shell around them.
 */
export function useCameraStream(deviceId: string): {
  videoRef: React.RefObject<HTMLVideoElement | null>;
  error: string | null;
} {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    setError(null);

    const codecs = supportedCodecs();
    if (!codecs) {
      setError('This display can’t play the camera feed.');
      return;
    }

    const mediaSource = new MediaSource();
    // An object URL rather than srcObject: MediaSource-as-srcObject is still
    // not universal, and this form works everywhere MSE itself does.
    const objectUrl = URL.createObjectURL(mediaSource);
    video.src = objectUrl;

    let socket: WebSocket | null = null;
    let sourceBuffer: SourceBuffer | null = null;
    // Fragments that arrived while the SourceBuffer was busy. appendBuffer is
    // one-at-a-time; calling it during an update throws InvalidStateError and
    // ends the feed, so everything queues here and drains on updateend.
    const pending: ArrayBuffer[] = [];
    let closed = false;

    const drain = () => {
      if (!sourceBuffer || sourceBuffer.updating || pending.length === 0) return;

      const next = pending.shift();
      if (!next) return;

      try {
        sourceBuffer.appendBuffer(next);
      } catch (err) {
        // QuotaExceededError despite the trimming below - a burst of large
        // keyframes, say. Dropping what is queued costs a visible hiccup;
        // letting it throw costs the feed.
        clientLogger.warn('Camera feed append failed; dropping queued fragments', {
          reason: err instanceof Error ? err.message : String(err),
        });
        pending.length = 0;
      }
    };

    const onUpdateEnd = () => {
      if (closed || !sourceBuffer || sourceBuffer.updating) return;

      // Trim before drift-correcting: removing a range moves buffered.start,
      // and the correction reads buffered.end, so doing it in this order means
      // one settled view of the buffer per cycle rather than two.
      const buffered = sourceBuffer.buffered;
      if (buffered.length > 0) {
        const trim = trimRange(video.currentTime, buffered.start(0));
        if (trim) {
          try {
            sourceBuffer.remove(trim.start, trim.end);
            return; // remove() is itself an update; drain on the next updateend.
          } catch {
            // A remove that races the stream ending. Nothing to do; the next
            // cycle tries again.
          }
        }

        const seekTo = driftCorrection(video.currentTime, buffered.end(buffered.length - 1));
        if (seekTo !== null) video.currentTime = seekTo;
      }

      drain();
    };

    const openSocket = () => {
      if (closed) return;

      // Same origin as the app, so the kiosk needs no camera address of its own
      // and the relay inherits whatever auth the page already passed.
      const scheme = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
      socket = new WebSocket(`${scheme}//${window.location.host}/api/devices/${deviceId}/camera/stream`);
      socket.binaryType = 'arraybuffer';

      socket.onopen = () => socket?.send(mseRequest(codecs));

      socket.onmessage = (event: MessageEvent<string | ArrayBuffer>) => {
        if (closed) return;

        if (typeof event.data !== 'string') {
          pending.push(event.data);
          drain();
          return;
        }

        const message = parseControlMessage(event.data);
        if (message.kind === 'error') {
          clientLogger.error('Camera feed refused', { deviceId, reason: message.message });
          setError('Camera feed unavailable.');
          return;
        }
        if (message.kind !== 'ready' || sourceBuffer) return;

        try {
          // message.mimeType verbatim: go2rtc answers with the codec the stream
          // actually carries, which need not be one of the ones we offered.
          sourceBuffer = mediaSource.addSourceBuffer(message.mimeType);
          sourceBuffer.mode = 'segments';
          sourceBuffer.addEventListener('updateend', onUpdateEnd);
          drain();
        } catch (err) {
          clientLogger.error('Camera feed codec rejected', {
            deviceId,
            mimeType: message.mimeType,
            reason: err instanceof Error ? err.message : String(err),
          });
          setError('Camera feed unavailable.');
        }
      };

      socket.onerror = () => {
        if (!closed) setError('Camera feed unavailable.');
      };
    };

    // The socket waits for sourceopen so a fragment can never arrive before
    // there is anywhere to put it.
    mediaSource.addEventListener('sourceopen', openSocket, { once: true });

    // Muted is not a preference: an autoplaying video with sound is blocked by
    // every browser's autoplay policy, and a modal that opens to a paused frame
    // of a camera is worse than no modal. The sub-stream carries no audio
    // anyway.
    video.muted = true;
    void video.play().catch(() => {
      // Interrupted by the modal closing, usually. The catch keeps it out of
      // the console as an unhandled rejection.
    });

    return () => {
      closed = true;
      mediaSource.removeEventListener('sourceopen', openSocket);
      sourceBuffer?.removeEventListener('updateend', onUpdateEnd);
      socket?.close();
      // Releases the relay's connection - and with it go2rtc's, and the
      // camera's - rather than leaving one open per modal the wall has ever
      // shown.
      URL.revokeObjectURL(objectUrl);
      video.removeAttribute('src');
      video.load();
    };
  }, [deviceId]);

  return { videoRef, error };
}
