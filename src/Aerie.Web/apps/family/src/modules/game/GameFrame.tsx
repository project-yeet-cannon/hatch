import { useCallback, useEffect, useRef, useState } from 'react';
import { frameDocument } from './frame';
import type { Breakage, GameRecord } from './types';

interface GameFrameProps {
  /** The game to run. Changing it swaps the running game in place - no reload. */
  code: string;
  /** Which version `code` came from, so a crash report names the right one. */
  versionId: string | null;
  records: GameRecord[];
  onError: (breakage: Breakage) => void;
  onRecords: (records: GameRecord[]) => void;
  onReady?: () => void;
}

/** Shape of the records the engine keeps, keyed by id (see engine.js `w.record`). */
type EngineRecords = Record<string, { id: string; label: string; value: number; unit?: string; lowerIsBetter?: boolean }>;

/**
 * The sandbox.
 *
 * `sandbox="allow-scripts"` and nothing else is the whole security story of
 * this app: no `allow-same-origin` means the frame gets an opaque origin, so
 * code written by a model cannot read the enrolled device's cookie, cannot call
 * Aerie's API, and cannot reach the shell around it. It can draw, and it can
 * postMessage. Adding `allow-same-origin` back would quietly undo all of that,
 * which is why the runtime is built to need nothing else.
 *
 * The iframe is mounted once and never remounted. New versions arrive as
 * messages, which is what makes a change land as the game visibly becoming
 * something else rather than as a white flash and a reload.
 */
export function GameFrame({ code, versionId, records, onError, onRecords, onReady }: GameFrameProps) {
  const frame = useRef<HTMLIFrameElement>(null);
  const [greeted, setGreeted] = useState(false);

  // Read inside the message handler and the load effect, neither of which may
  // re-run just because a parent re-rendered with new closures.
  const latest = useRef({ versionId, records, onError, onRecords, onReady });
  latest.current = { versionId, records, onError, onRecords, onReady };

  const post = useCallback((message: unknown) => {
    // The frame's origin is opaque, so '*' is the only target it can be given.
    // Nothing sent down is secret - it is the game's own source and its scores.
    frame.current?.contentWindow?.postMessage(message, '*');
  }, []);

  useEffect(() => {
    function onMessage(event: MessageEvent) {
      // Only our frame's window. Anything else on the page - another module, an
      // extension, an ad-blocker's helper - is not the game talking.
      if (!frame.current || event.source !== frame.current.contentWindow) return;

      const data = event.data as { type?: string; phase?: string; message?: string; stack?: string; records?: EngineRecords };
      if (typeof data?.type !== 'string') return;

      if (data.type === 'aerie:hello') {
        setGreeted(true);
      } else if (data.type === 'aerie:ready') {
        latest.current.onReady?.();
      } else if (data.type === 'aerie:error') {
        latest.current.onError({
          versionId: latest.current.versionId ?? '',
          phase: data.phase ?? 'unknown',
          message: data.message ?? 'Something went wrong.',
          stack: data.stack ?? null,
        });
      } else if (data.type === 'aerie:records' && data.records) {
        latest.current.onRecords(
          Object.values(data.records).map((record) => ({
            id: record.id,
            label: record.label,
            value: record.value,
            unit: record.unit ?? null,
            lowerIsBetter: record.lowerIsBetter === true,
          })),
        );
      }
    }

    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, []);

  /*
    New code restarts the game. Nothing else does - and in particular a change
    to `records` must not, which is why they are read from the ref above rather
    than being a dependency: a personal best originates *in* the frame, and
    restarting the game on the way back from saving one would wipe out the run
    a child had just done well on.

    They still travel with the load rather than in a message of their own,
    because the engine seeds itself with them during setup. Arriving a moment
    later would mean every fresh version briefly believing no record had ever
    been set, and announcing the first jump of the day as a new best.
  */
  useEffect(() => {
    if (!greeted) return;
    const seeded: EngineRecords = {};
    for (const record of latest.current.records) {
      seeded[record.id] = {
        id: record.id,
        label: record.label,
        value: record.value,
        unit: record.unit ?? '',
        lowerIsBetter: record.lowerIsBetter,
      };
    }
    post({ type: 'aerie:load', code, records: seeded });
  }, [greeted, code, post]);

  return (
    <iframe
      ref={frame}
      className="game-frame"
      title="Game"
      sandbox="allow-scripts"
      srcDoc={frameDocument}
    />
  );
}
