import type { PanelItemState, PanelSource, PanelState, PanelSummary } from '../types';

/**
 * Panels' half of the all-X / all-9999 source (see testDataSource.ts): every
 * label is X, every temperature is 9999, so anything the tile row or the
 * overlay draws from its own hardcoded strings stands out immediately. Long
 * labels are the point on a wall display - a control name that has to wrap or
 * truncate has to do it somewhere, and this is where that shows up.
 *
 * Writes are accepted and change nothing. This source exists to expose
 * hardcoded content, not to model behaviour; mockPanelSource is where power and
 * setpoint actually move.
 */

const X = 'XXXX';
const X_LONG = 'XXXXXXXXXXXXXXXX';
const X_LONGEST = 'XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX';
const NUM = 9999;

/** The tile row's half, exported for testDataSource to hang on the snapshot. */
export const TEST_PANELS: PanelSummary[] = [1, 2].map((i) => ({
  id: `${X}-panel-${i}`,
  name: i === 1 ? X_LONG : X,
  icon: 'certificate',
  color: '#ff00ff',
}));

function base(id: string): PanelItemState {
  return {
    id,
    kind: 'Control',
    label: X_LONG,
    icon: 'certificate',
    color: '#ff00ff',
    controlKind: 'Switch',
    isOn: null,
    setpointF: null,
    ambientF: null,
    mode: null,
    minF: null,
    maxF: null,
    stepF: null,
    routineId: null,
    isToggle: null,
    isActive: null,
  };
}

/**
 * One of each shape the overlay can be handed, including the two that have no
 * value to print: a switch that has never reported, and a thermostat whose
 * ambient is unbound. Both must render as absence rather than as a 0.
 */
function xItems(panelId: string): PanelItemState[] {
  return [
    {
      ...base(`${panelId}-thermostat`),
      label: X_LONGEST,
      controlKind: 'Thermostat',
      isOn: true,
      setpointF: NUM,
      ambientF: NUM,
      mode: X,
      // Bounds stay a usable range: NUM for all three would make every step
      // button disabled and hide the control this source exists to inspect.
      minF: 60,
      maxF: 85,
      stepF: 1,
    },
    { ...base(`${panelId}-thermostat-bare`), controlKind: 'Thermostat', isOn: null, setpointF: NUM, minF: 60, maxF: 85, stepF: 1 },
    { ...base(`${panelId}-switch-on`), isOn: true },
    { ...base(`${panelId}-switch-unknown`) },
    {
      ...base(`${panelId}-routine`),
      kind: 'Routine',
      controlKind: null,
      routineId: `${X}-routine`,
      isToggle: true,
      isActive: false,
    },
  ];
}

export class TestPanelSource implements PanelSource {
  getState(panelId: string): Promise<PanelState> {
    return Promise.resolve({ id: panelId, name: X_LONG, items: xItems(panelId) });
  }

  setPower(_panelId: string, _itemId: string, _on: boolean): Promise<void> {
    return Promise.resolve();
  }

  setSetpoint(_panelId: string, _itemId: string, _valueF: number): Promise<void> {
    return Promise.resolve();
  }
}
