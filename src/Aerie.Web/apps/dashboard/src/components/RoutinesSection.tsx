import { useState } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import type { RoutineSummary } from '../types';
import { triggerRoutine } from '../api/routinesClient';
import { clientLogger } from '../lib/clientLogger';
import { iconFor } from '../lib/icons';

/** A tap-to-trigger button per Routine, rendered below the zones. The dashboard is otherwise pure read-only polling display, so each button owns its own pending/error state around the trigger call rather than relying on any app-wide pattern. */
export function RoutinesSection({ routines }: { routines: RoutineSummary[] }) {
  const [pendingId, setPendingId] = useState<string | null>(null);
  const [status, setStatus] = useState<{ id: string; error: string } | null>(null);

  async function handleTap(routine: RoutineSummary) {
    if (pendingId) return;
    setPendingId(routine.id);
    setStatus(null);
    try {
      await triggerRoutine(routine.id);
      clientLogger.info('Routine triggered', { routineId: routine.id, name: routine.name });
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
        return (
          <button
            key={routine.id}
            type="button"
            className="hf-routine-btn"
            disabled={pending}
            onClick={() => handleTap(routine)}
          >
            <span
              className="hf-routine-circle"
              style={{
                background: routine.color ? `color-mix(in srgb, ${routine.color} 16%, transparent)` : 'var(--line)',
                boxShadow: pending && routine.color ? `0 0 0 5px color-mix(in srgb, ${routine.color} 35%, transparent)` : undefined,
                transform: pending ? 'scale(1.08)' : undefined,
              }}
            >
              <FontAwesomeIcon icon={iconFor(routine.icon)} className="hf-routine-icon" style={{ color: routine.color ?? undefined }} />
            </span>
            {pending ? (
              <span className="hf-routine-status">Triggering…</span>
            ) : status?.id === routine.id ? (
              <span className="hf-routine-status hf-routine-error">Couldn’t trigger — {status.error}</span>
            ) : (
              <span className="hf-routine-name">{routine.name}</span>
            )}
          </button>
        );
      })}
    </div>
  );
}
