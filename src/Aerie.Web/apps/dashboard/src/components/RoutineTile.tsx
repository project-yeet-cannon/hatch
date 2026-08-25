import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { iconFor } from '../lib/icons';

/**
 * One routine tile — the square icon-and-name button the dashboard row is made
 * of, and the same one a routine sitting inside a Panel overlay renders as.
 * Sharing the component rather than the look is deliberate: a routine is the
 * same thing in both places, and a person who has learned what it does on the
 * dashboard should not have to learn it again inside a panel.
 *
 * Purely presentational. Everything about what a tap *does* — the optimistic
 * toggle, the in-flight guard, the failure — is hooks/useRoutineTaps.ts.
 */
export function RoutineTile({
  name,
  icon,
  color,
  isToggle,
  isActive,
  pending,
  error,
  onTap,
}: {
  name: string;
  icon: string | null;
  color: string | null;
  isToggle: boolean;
  isActive: boolean;
  pending: boolean;
  error: string | null;
  onTap: () => void;
}) {
  const fillPct = pending ? 35 : isActive ? 30 : 16;
  return (
    <button
      type="button"
      className={`hf-routine-btn${isActive ? ' active' : ''}`}
      disabled={pending}
      onClick={onTap}
    >
      <span
        className="hf-routine-circle"
        style={{
          background: color ? `color-mix(in srgb, ${color} ${fillPct}%, transparent)` : 'var(--line)',
          boxShadow:
            (pending || isActive) && color
              ? `0 0 0 5px color-mix(in srgb, ${color} ${pending ? 35 : 22}%, transparent)`
              : undefined,
          transform: pending ? 'scale(1.08)' : undefined,
        }}
      >
        <FontAwesomeIcon icon={iconFor(icon)} className="hf-routine-icon" style={{ color: color ?? undefined }} />
      </span>
      {pending ? (
        <span className="hf-routine-status">{isActive ? 'Turning off…' : 'Triggering…'}</span>
      ) : error !== null ? (
        <span className="hf-routine-status hf-routine-error">Couldn’t trigger — {error}</span>
      ) : (
        <span className="hf-routine-name">
          {name}
          {isToggle && <span className="hf-routine-onoff">{isActive ? 'On' : 'Off'}</span>}
        </span>
      )}
    </button>
  );
}
