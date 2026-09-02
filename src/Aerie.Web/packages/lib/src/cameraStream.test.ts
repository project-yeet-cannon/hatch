import { describe, expect, it } from 'vitest';
import {
  driftCorrection,
  LIVE_EDGE_MARGIN_SECONDS,
  MAX_BUFFER_SECONDS,
  MAX_DRIFT_SECONDS,
  mseRequest,
  parseControlMessage,
  supportedCodecs,
  trimRange,
} from './cameraStream';

describe('supportedCodecs', () => {
  it('offers only what the browser reports it can decode', () => {
    const codecs = supportedCodecs((type) => type.includes('avc1.42e01e'));

    expect(codecs).toBe('avc1.42e01e');
  });

  it('joins several as one comma-separated value, which is the shape go2rtc parses', () => {
    const codecs = supportedCodecs((type) => type.includes('avc1.640029') || type.includes('avc1.42e01e'));

    expect(codecs).toBe('avc1.640029,avc1.42e01e');
  });

  // A browser with no MSE H.264 at all. The caller checks for empty rather than
  // opening a socket that can only end in an error frame.
  it('is empty when nothing is supported', () => {
    expect(supportedCodecs(() => false)).toBe('');
  });

  // Every candidate is H.264 on purpose: the kiosk watches the camera's
  // sub-stream, because the main stream on the 4K models is H.265 and would
  // need a transcode per viewer.
  it('never offers H.265', () => {
    expect(supportedCodecs(() => true)).not.toMatch(/hvc1|hev1/);
  });
});

describe('mseRequest', () => {
  it('is the frame go2rtc answers with a MIME type', () => {
    expect(mseRequest('avc1.640029')).toBe('{"type":"mse","value":"avc1.640029"}');
  });
});

describe('parseControlMessage', () => {
  // The exact reply observed from go2rtc 1.9.14, including that the codec
  // returned (640029) is not the one requested (640028) - which is why the
  // caller must use this string verbatim.
  it('reads the accept reply as the MIME type to hand addSourceBuffer', () => {
    const message = parseControlMessage('{"type":"mse","value":"video/mp4; codecs=\\"avc1.640029\\""}');

    expect(message).toEqual({ kind: 'ready', mimeType: 'video/mp4; codecs="avc1.640029"' });
  });

  it('reads a refusal, keeping go2rtc’s own reason', () => {
    const message = parseControlMessage('{"type":"error","value":"mse: stream not found"}');

    expect(message).toEqual({ kind: 'error', message: 'mse: stream not found' });
  });

  it('falls back to a generic reason for an error with no text', () => {
    expect(parseControlMessage('{"type":"error"}')).toEqual({
      kind: 'error',
      message: 'Camera stream refused',
    });
  });

  // go2rtc sends message types this client has no use for. Treating an unknown
  // one as an error would tear down a working feed.
  it.each([
    ['another message type', '{"type":"webrtc","value":"..."}'],
    ['an mse reply with no value', '{"type":"mse"}'],
    ['a non-object', '"hello"'],
    ['null', 'null'],
    ['not JSON at all', 'not json'],
  ])('ignores %s', (_label, raw) => {
    expect(parseControlMessage(raw)).toEqual({ kind: 'ignored' });
  });
});

describe('driftCorrection', () => {
  it('leaves the playhead alone while it is near the live edge', () => {
    expect(driftCorrection(9.5, 10)).toBeNull();
  });

  it('is inclusive at the threshold, so a feed sitting exactly on it is not nudged every frame', () => {
    expect(driftCorrection(10 - MAX_DRIFT_SECONDS, 10)).toBeNull();
  });

  // The case that matters on a kiosk: the tab was backgrounded between motion
  // events and comes back seconds behind, showing footage of what already
  // happened.
  it('jumps to just behind the live edge once it has drifted', () => {
    expect(driftCorrection(2, 10)).toBeCloseTo(10 - LIVE_EDGE_MARGIN_SECONDS);
  });

  it('never moves the playhead backwards', () => {
    expect(driftCorrection(10, 9)).toBeNull();
  });

  it.each([
    ['nothing buffered yet', 0],
    ['a NaN buffered end, which is what an empty TimeRanges gives', Number.NaN],
  ])('does nothing with %s', (_label, bufferedEnd) => {
    expect(driftCorrection(0, bufferedEnd)).toBeNull();
  });
});

describe('trimRange', () => {
  // Without this the SourceBuffer grows for as long as the modal is open and
  // an append eventually throws QuotaExceededError, killing the feed.
  it('evicts everything older than the retention window', () => {
    expect(trimRange(100, 0)).toEqual({ start: 0, end: 100 - MAX_BUFFER_SECONDS });
  });

  it('does nothing while the buffer is still inside the window', () => {
    expect(trimRange(10, 0)).toBeNull();
  });

  it('does nothing when the buffer already starts at the cutoff', () => {
    expect(trimRange(100, 100 - MAX_BUFFER_SECONDS)).toBeNull();
  });

  it('does nothing with an empty buffer', () => {
    expect(trimRange(100, Number.NaN)).toBeNull();
  });
});
