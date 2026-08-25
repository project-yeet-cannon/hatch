import { useCallback, useEffect, useRef } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faMinus, faPlus } from '@fortawesome/free-solid-svg-icons';
import type { PanelItemState } from '../types';
import { iconFor } from '../lib/icons';
import { PowerButtons } from './SwitchControl';

/**
 * A Panel's thermostat — the AC today, a radiator later, differing only in
 * which HVAC mode counts as "on", which is the server's business and not this
 * component's.
 *
 * The interaction the whole design turns on: ⊖ and ⊕ move the number
 * immediately and locally, and the write is dispatched only once the buttons go
 * quiet (usePanelState's settle). Holding "+" from 68 to 78 is one decision at
 * the wall and lands as one ledgered SetTemperature, not ten.
 *
 * The bounds are enforced here as well as server-side, and that duplication is
 * the point: a ⊕ that is visibly disabled at the top of the range tells someone
 * why nothing is happening, where a silently-clamped write leaves them pressing
 * a button that has already stopped meaning anything.
 */

/** Long enough that a tap is never a hold; short enough that a hold doesn't feel stuck. */
const HOLD_DELAY_MS = 400;

/** ~7 steps a second — a 25° range crosses in under four, and no single step is missable. */
const HOLD_REPEAT_MS = 150;

export function ThermostatControl({
  item,
  busy,
  error,
  onSetPower,
  onSetSetpoint,
}: {
  item: PanelItemState;
  busy: boolean;
  error: string | null;
  onSetPower: (on: boolean) => void;
  onSetSetpoint: (valueF: number) => void;
}) {
  const min = item.minF ?? 60;
  const max = item.maxF ?? 85;
  const step = item.stepF ?? 1;
  const setpoint = item.setpointF;

  // The current value, readable from inside the repeat timer. The overlay's
  // optimistic overlay already makes `item.setpointF` follow each step, so this
  // only exists so two ticks landing between renders still both count.
  const valueRef = useRef<number | null>(setpoint);
  const holdRef = useRef<{ delay: ReturnType<typeof setTimeout> | null; repeat: ReturnType<typeof setInterval> | null }>({
    delay: null,
    repeat: null,
  });

  const endHold = useCallback(() => {
    if (holdRef.current.delay !== null) clearTimeout(holdRef.current.delay);
    if (holdRef.current.repeat !== null) clearInterval(holdRef.current.repeat);
    holdRef.current = { delay: null, repeat: null };
  }, []);

  useEffect(() => endHold, [endHold]);

  useEffect(() => {
    if (holdRef.current.delay === null && holdRef.current.repeat === null) valueRef.current = setpoint;
  }, [setpoint]);

  const nudge = useCallback(
    (direction: 1 | -1) => {
      const current = valueRef.current;
      if (current === null) return;
      const next = clamp(snap(current + direction * step, min, step), min, max);
      if (next === current) {
        // Already at the bound - stop repeating rather than spinning a timer
        // that can't change anything.
        endHold();
        return;
      }
      valueRef.current = next;
      onSetSetpoint(next);
    },
    [endHold, max, min, onSetSetpoint, step],
  );

  const beginHold = useCallback(
    (direction: 1 | -1) => {
      endHold();
      nudge(direction);
      holdRef.current.delay = setTimeout(() => {
        holdRef.current.repeat = setInterval(() => nudge(direction), HOLD_REPEAT_MS);
      }, HOLD_DELAY_MS);
    },
    [endHold, nudge],
  );

  const label = item.label ?? 'Thermostat';
  const unknown = setpoint === null;
  const atMin = setpoint !== null && setpoint <= min;
  const atMax = setpoint !== null && setpoint >= max;

  return (
    <div className="hf-panel-card">
      <div className="hf-panel-card-head">
        <span className="hf-panel-card-icon" style={{ color: item.color ?? undefined }} aria-hidden="true">
          <FontAwesomeIcon icon={iconFor(item.icon)} />
        </span>
        <span className="hf-panel-card-label">{label}</span>
      </div>

      {/* Rendered unconditionally: the state DTO reports null for a thermostat
          with no on/off bound and for one whose channel has simply never
          reported, deliberately (see PanelItemState), so the kiosk cannot tell
          those apart. A setpoint-only thermostat is legal but unusual, and the
          server answers a tap on one with the reason, which lands in the error
          line below. */}
      <PowerButtons isOn={item.isOn} busy={busy} label={label} onSetPower={onSetPower} />

      <div className="hf-panel-set">
        <StepButton
          direction={-1}
          ariaLabel={`Lower ${label}`}
          disabled={unknown || atMin}
          onBegin={beginHold}
          onEnd={endHold}
        />
        <span className="hf-panel-temp" aria-live="polite" aria-label={`${label} setpoint`}>
          {/* The em dash stands alone: "—°" reads as a temperature that happens
              to be missing a digit, where "—" reads as no reading at all,
              which is what a setpoint channel that has never reported is. */}
          {unknown ? (
            '—'
          ) : (
            <>
              {formatF(setpoint, step)}
              <span className="hf-panel-degree">°</span>
            </>
          )}
        </span>
        <StepButton
          direction={1}
          ariaLabel={`Raise ${label}`}
          disabled={unknown || atMax}
          onBegin={beginHold}
          onEnd={endHold}
        />
      </div>

      {/* Whole degrees, the way every other temperature on this wall is drawn
          (ZoneCard, OutsideCard) - the setpoint is the one number here precise
          enough to want a decimal, and only when its step is. */}
      <p className="hf-panel-ambient">{item.ambientF === null ? ' ' : `Now ${Math.round(item.ambientF)}°`}</p>
      {error !== null && <p className="hf-panel-error">Couldn’t set — {error}</p>}
    </div>
  );
}

/**
 * Pointer events rather than onClick: the first step has to land on press so a
 * tap feels instant, and press/release is also what bounds the hold.
 *
 * The press captures the pointer, which is what makes a finger that drifts off
 * the button mid-hold safe — the release still lands here rather than on
 * whatever is now under it, so the repeat always has an event that ends it.
 * `lostpointercapture` is the backstop under that: it fires on release, on
 * cancel, and if the button disables itself at the end of the range, so there
 * is no path where the timer outlives the touch. Deliberately no
 * `pointerleave`, which capture suppresses until release and which would only
 * be a second, differently-timed way to say the same thing.
 */
function StepButton({
  direction,
  ariaLabel,
  disabled,
  onBegin,
  onEnd,
}: {
  direction: 1 | -1;
  ariaLabel: string;
  disabled: boolean;
  onBegin: (direction: 1 | -1) => void;
  onEnd: () => void;
}) {
  return (
    <button
      type="button"
      className="hf-panel-step"
      aria-label={ariaLabel}
      disabled={disabled}
      onPointerDown={(event) => {
        event.currentTarget.setPointerCapture(event.pointerId);
        onBegin(direction);
      }}
      onPointerUp={onEnd}
      onLostPointerCapture={onEnd}
    >
      <FontAwesomeIcon icon={direction === 1 ? faPlus : faMinus} />
    </button>
  );
}

/** Snaps to the step measured from the minimum, so every value the buttons reach is one the server's own Snap would also land on. */
function snap(value: number, min: number, step: number): number {
  return min + Math.round((value - min) / step) * step;
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

/** Whole degrees for a whole-degree step; one decimal otherwise. A wall display reading "72.0" is noise, and one reading "72" when the step is half a degree is a lie. */
function formatF(value: number, step: number): string {
  return step >= 1 ? String(Math.round(value)) : value.toFixed(1);
}
