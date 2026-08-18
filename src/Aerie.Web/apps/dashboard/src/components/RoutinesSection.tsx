import { useEffect, useState } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import type { RoutineSummary } from '../types';
import { triggerRoutine, turnOffRoutine } from '../api/routinesClient';
import { clientLogger } from '../lib/clientLogger';
import { iconFor } from '../lib/icons';

/**
 * A tap-to-trigger button per Routine, rendered below the zones. The
 * dashboard is otherwise pure read-only polling display, so each button owns
 * its own pending/error state around the trigger call rather than relying on
 * any app-wide pattern.
 *
 * A toggle routine (isToggle) additionally reads active/inactive from
 * routine.isActive and flips direction on tap - trigger (run Actions, i.e.
 * turn on) when inactive, turn-off (invert the SetPower actions) when
 * active. isActive only refreshes on the next 60s poll, so a local
 * optimisticActive map reflects the tap immediately; it's cleared per-routine
 * once the server snapshot agrees, so an external change (e.g. the light
 * flipped from Home Assistant directly) still surfaces on the next poll.
 */
export function RoutinesSection({ routines }: { routines: RoutineSummary[] }) {
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [status, setStatus] = useState<{ id: string; error: string } | null>(null);
  const [optimisticActive, setOptimisticActive] = useState<Record<string, boolean>>({});

  useEffect(() => {
    setOptimisticActive((prev) => {
      let changed = false;
      const next = { ...prev };
      for (const routine of routines) {
        if (routine.id in next && next[routine.id] === routine.isActive) {
          delete next[routine.id];
          changed = true;
        }
      }
      return changed ? next : prev;
    });
  }, [routines]);

  async function handleTap(routine: RoutineSummary, isActive: boolean) {
    if (pendingId) return;
    setPendingId(routine.id);
    setStatus(null);
    try {
      if (routine.isToggle && isActive) {
        await turnOffRoutine(routine.id);
        clientLogger.info('Routine turned off', { routineId: routine.id, name: routine.name });
      } else {
        await triggerRoutine(routine.id);
        clientLogger.info('Routine triggered', { routineId: routine.id, name: routine.name });
      }
      if (routine.isToggle) {
        setOptimisticActive((prev) => ({ ...prev, [routine.id]: !isActive }));
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      clientLogger.error('Routine trigger failed', { routineId: routine.id, reason: message });
      setStatus({ id: routine.id, error: message });
    } finally {
      setPendingId(null);
    }
  }

  return (
    <div className="hf-routines">
      {routines.map((routine) => {
        const pending = pendingId === routine.id;
        const isActive = routine.isToggle && (optimisticActive[routine.id] ?? routine.isActive ?? false);
        const fillPct = pending ? 35 : isActive ? 30 : 16;
        return (
          <button
            key={routine.id}
            type="button"
            className={`hf-routine-btn${isActive ? ' active' : ''}`}
            disabled={pending}
            onClick={() => handleTap(routine, isActive)}
          >
            <span
              className="hf-routine-circle"
              style={{
                background: routine.color ? `color-mix(in srgb, ${routine.color} ${fillPct}%, transparent)` : 'var(--line)',
                boxShadow:
                  (pending || isActive) && routine.color
                    ? `0 0 0 5px color-mix(in srgb, ${routine.color} ${pending ? 35 : 22}%, transparent)`
                    : undefined,
                transform: pending ? 'scale(1.08)' : undefined,
              }}
            >
              <FontAwesomeIcon icon={iconFor(routine.icon)} className="hf-routine-icon" style={{ color: routine.color ?? undefined }} />
            </span>
            {pending ? (
              <span className="hf-routine-status">{isActive ? 'Turning off…' : 'Triggering…'}</span>
            ) : status?.id === routine.id ? (
              <span className="hf-routine-status hf-routine-error">Couldn’t trigger — {status.error}</span>
            ) : (
              <span className="hf-routine-name">
                {routine.name}
                {routine.isToggle && <span className="hf-routine-onoff">{isActive ? 'On' : 'Off'}</span>}
              </span>
            )}
          </button>
        );
      })}
    </div>
  );
}
