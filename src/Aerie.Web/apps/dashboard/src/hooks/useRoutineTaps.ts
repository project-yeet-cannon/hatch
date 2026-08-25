import { useCallback, useEffect, useState } from 'react';
import { triggerRoutine, turnOffRoutine } from '../api/routinesClient';
import { clientLogger } from '../lib/clientLogger';

/**
 * The behaviour half of "tap a routine": which one is in flight, what failed,
 * and the optimistic active state that makes a toggle answer the finger rather
 * than the next poll. Split out from RoutinesSection when Panels arrived,
 * because a routine sitting inside a panel overlay has to behave identically to
 * the same routine's tile on the dashboard — and two copies of this would drift
 * the first time one of them was fixed.
 *
 * Geometry lives in components/RoutineTile.tsx; this file has no opinion about
 * how a routine looks, only about what tapping one does.
 */

/**
 * The least a caller has to know about a routine to tap it. RoutineSummary
 * satisfies this structurally, and a panel's routine item is mapped onto it —
 * which is what lets the two surfaces share this without sharing a DTO.
 */
export interface RoutineTapTarget {
  /** The Routine's own id — what /api/routines/{id} takes, never a panel item id. */
  id: string;
  name: string;
  isToggle: boolean;
  isActive: boolean | null;
}

export interface RoutineTaps {
  /** What the tile should draw: the optimistic answer while one is outstanding, the server's otherwise. */
  isActive(target: RoutineTapTarget): boolean;
  isPending(target: RoutineTapTarget): boolean;
  errorFor(target: RoutineTapTarget): string | null;
  tap(target: RoutineTapTarget): void;
}

/**
 * @param targets The routines currently on screen. Their server-reported
 * isActive is what the optimistic overrides reconcile against, so this wants
 * the freshest list the caller has — every poll, not just the first.
 * @param resetToken The kiosk's idle reset (hooks/useKioskLifecycle.ts). Only
 * the error message clears on it: a failure nobody is standing in front of any
 * more shouldn't sit on the wall indefinitely, while an optimistic tick must
 * survive or a tap would visually revert until the next poll.
 */
export function useRoutineTaps(targets: RoutineTapTarget[], resetToken?: number): RoutineTaps {
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [status, setStatus] = useState<{ id: string; error: string } | null>(null);
  const [optimisticActive, setOptimisticActive] = useState<Record<string, boolean>>({});

  useEffect(() => {
    setOptimisticActive((prev) => {
      let changed = false;
      const next = { ...prev };
      for (const target of targets) {
        if (target.id in next && next[target.id] === target.isActive) {
          delete next[target.id];
          changed = true;
        }
      }
      return changed ? next : prev;
    });
  }, [targets]);

  useEffect(() => {
    setStatus(null);
  }, [resetToken]);

  const isActive = useCallback(
    (target: RoutineTapTarget) => target.isToggle && (optimisticActive[target.id] ?? target.isActive ?? false),
    [optimisticActive],
  );

  const tap = useCallback(
    (target: RoutineTapTarget) => {
      // One at a time across every routine on screen, not one per tile: a run
      // of taps down a row is a person being impatient, not four decisions.
      if (pendingId) return;
      const active = target.isToggle && (optimisticActive[target.id] ?? target.isActive ?? false);
      setPendingId(target.id);
      setStatus(null);

      const call = target.isToggle && active ? turnOffRoutine(target.id) : triggerRoutine(target.id);
      void call
        .then(() => {
          clientLogger.info(active ? 'Routine turned off' : 'Routine triggered', { routineId: target.id, name: target.name });
          if (target.isToggle) setOptimisticActive((prev) => ({ ...prev, [target.id]: !active }));
        })
        .catch((err: unknown) => {
          const message = err instanceof Error ? err.message : String(err);
          clientLogger.error('Routine trigger failed', { routineId: target.id, reason: message });
          setStatus({ id: target.id, error: message });
        })
        .finally(() => setPendingId(null));
    },
    [optimisticActive, pendingId],
  );

  return {
    isActive,
    isPending: (target) => pendingId === target.id,
    errorFor: (target) => (status?.id === target.id ? status.error : null),
    tap,
  };
}
