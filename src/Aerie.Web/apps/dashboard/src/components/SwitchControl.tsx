import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import type { PanelItemState } from '../types';
import { iconFor } from '../lib/icons';

/**
 * A Panel's on/off control — a fan, a lamp, anything whose whole interface is
 * one bit. Two big targets rather than one toggle: the write is absolute
 * (POST .../power takes { on }, not a flip), and giving the finger the same
 * absolute choice means a wall showing a stale state can be wrong about what it
 * *displays* and never about what it *sends*.
 *
 * isOn === null is drawn as neither button selected, not as Off. A control
 * whose channel has never reported is unknown, and a confident "Off" for a
 * device nobody has heard from is the more expensive lie: it is the one that
 * makes someone stop looking for the problem.
 */
export function SwitchControl({
  item,
  busy,
  error,
  onSetPower,
}: {
  item: PanelItemState;
  busy: boolean;
  error: string | null;
  onSetPower: (on: boolean) => void;
}) {
  return (
    <div className="hf-panel-card">
      <div className="hf-panel-card-head">
        <span className="hf-panel-card-icon" style={{ color: item.color ?? undefined }} aria-hidden="true">
          <FontAwesomeIcon icon={iconFor(item.icon)} />
        </span>
        <span className="hf-panel-card-label">{item.label ?? 'Switch'}</span>
      </div>
      <PowerButtons isOn={item.isOn} busy={busy} label={item.label ?? 'Switch'} onSetPower={onSetPower} />
      {error !== null && <p className="hf-panel-error">Couldn’t switch — {error}</p>}
    </div>
  );
}

/**
 * Shared with ThermostatControl, which needs exactly this pair above its
 * setpoint. Kept here rather than in a file of its own because a thermostat's
 * On/Off *is* a switch — the only difference is which channel the write lands
 * on, and that is the server's business.
 */
export function PowerButtons({
  isOn,
  busy,
  label,
  onSetPower,
}: {
  isOn: boolean | null;
  busy: boolean;
  label: string;
  onSetPower: (on: boolean) => void;
}) {
  return (
    <div className="hf-panel-power" role="group" aria-label={`${label} power`}>
      <button
        type="button"
        className={`hf-panel-power-btn${isOn === false ? ' on' : ''}`}
        aria-pressed={isOn === false}
        disabled={busy}
        onClick={() => onSetPower(false)}
      >
        Off
      </button>
      <button
        type="button"
        className={`hf-panel-power-btn${isOn === true ? ' on' : ''}`}
        aria-pressed={isOn === true}
        disabled={busy}
        onClick={() => onSetPower(true)}
      >
        On
      </button>
    </div>
  );
}
