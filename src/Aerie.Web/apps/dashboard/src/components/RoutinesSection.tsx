import type { RoutineSummary } from '../types';
import { useRoutineTaps } from '../hooks/useRoutineTaps';
import { RoutineTile } from './RoutineTile';

/**
 * A tap-to-trigger button per Routine, rendered below the zones. The dashboard
 * is otherwise pure read-only polling display, so tapping owns its own
 * pending/error state rather than relying on any app-wide pattern — that
 * behaviour is hooks/useRoutineTaps.ts, and the tile itself is RoutineTile.
 * Both are shared with the Panel overlay, where a routine can also appear.
 *
 * A toggle routine (isToggle) reads active/inactive from routine.isActive and
 * flips direction on tap — trigger (run Actions, i.e. turn on) when inactive,
 * turn-off (invert the SetPower actions) when active.
 *
 * resetToken is the kiosk's idle reset (hooks/useKioskLifecycle.ts); see the
 * hook for what does and doesn't clear on it.
 */
export function RoutinesSection({ routines, resetToken }: { routines: RoutineSummary[]; resetToken?: number }) {
  const taps = useRoutineTaps(routines, resetToken);

  return (
    <div className="hf-routines">
      {routines.map((routine) => (
        <RoutineTile
          key={routine.id}
          name={routine.name}
          icon={routine.icon}
          color={routine.color}
          isToggle={routine.isToggle}
          isActive={taps.isActive(routine)}
          pending={taps.isPending(routine)}
          error={taps.errorFor(routine)}
          onTap={() => taps.tap(routine)}
        />
      ))}
    </div>
  );
}
