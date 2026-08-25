import type { PanelItemState, PanelSource, PanelState, PanelSummary } from '../types';

/**
 * Panels with no API behind them: a synthetic Climate panel whose controls
 * actually change, so `?source=mock` is a usable surface for working on the
 * overlay rather than a screenshot of one. Power flips, the setpoint moves and
 * snaps to the control's step, and both survive the 5s poll — an overlay that
 * only looks right against data that never changes isn't verified at all.
 *
 * The server semantics reproduced here are PanelService.GetStateAsync and
 * PanelsController's Snap; if the two ever disagree, those are right.
 */

const LATENCY_MS = 120;

/** Shaped like Guids so nothing downstream can quietly depend on the format. */
const PANEL_ID = '00000000-0000-4000-9000-000000000001';
const AC_ID = '00000000-0000-4000-9000-000000000002';
const FAN_ONE_ID = '00000000-0000-4000-9000-000000000003';
const FAN_TWO_ID = '00000000-0000-4000-9000-000000000004';
const ROUTINE_ITEM_ID = '00000000-0000-4000-9000-000000000005';

/** Matches MockDashboardDataSource's ROUTINES, so a panel routine item is the same routine as the tile above it. */
const MAX_AC_ROUTINE_ID = 'max-ac';

/** The tile row's half, exported for mockDataSource to hang on the snapshot. */
export const MOCK_PANELS: PanelSummary[] = [{ id: PANEL_ID, name: 'Climate', icon: 'temperature-half', color: '#3ba3d6' }];

/**
 * Deliberately uneven: a thermostat that has reported everything, one fan on,
 * one fan that has never reported at all (isOn null — the case the switch is
 * easiest to get wrong, since "unknown" must not draw as "off"), and a routine.
 */
const ITEMS: PanelItemState[] = [
  {
    id: AC_ID,
    kind: 'Control',
    label: 'Air conditioner',
    icon: 'snowflake',
    color: '#3ba3d6',
    controlKind: 'Thermostat',
    isOn: true,
    setpointF: 72,
    ambientF: 76.4,
    mode: 'cool',
    minF: 60,
    maxF: 85,
    stepF: 1,
    routineId: null,
    isToggle: null,
    isActive: null,
  },
  {
    id: FAN_ONE_ID,
    kind: 'Control',
    label: 'Fan 1',
    icon: 'fan',
    color: '#7cc4a4',
    controlKind: 'Switch',
    isOn: true,
    setpointF: null,
    ambientF: null,
    mode: null,
    minF: null,
    maxF: null,
    stepF: null,
    routineId: null,
    isToggle: null,
    isActive: null,
  },
  {
    id: FAN_TWO_ID,
    kind: 'Control',
    label: 'Fan 2',
    icon: 'fan',
    color: '#7cc4a4',
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
  },
  {
    id: ROUTINE_ITEM_ID,
    kind: 'Routine',
    label: 'Max AC',
    icon: 'snowflake',
    color: '#3ba3d6',
    controlKind: null,
    isOn: null,
    setpointF: null,
    ambientF: null,
    mode: null,
    minF: null,
    maxF: null,
    stepF: null,
    routineId: MAX_AC_ROUTINE_ID,
    isToggle: null,
    isActive: null,
  },
];

function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), LATENCY_MS));
}

function find(itemId: string): PanelItemState {
  const item = ITEMS.find((i) => i.id === itemId);
  if (!item) throw new Error(`No such panel item: ${itemId}`);
  return item;
}

/** PanelsController.Snap: clamp, snap to the nearest step measured from the minimum, clamp again. */
function snap(value: number, min: number, max: number, step: number): number {
  const clamped = Math.min(Math.max(value, min), max);
  const snapped = min + Math.round((clamped - min) / step) * step;
  return Math.min(Math.max(snapped, min), max);
}

export class MockPanelSource implements PanelSource {
  getState(panelId: string): Promise<PanelState> {
    // Shallow copies, so a caller holding an earlier snapshot doesn't see this
    // one's values change under it - the API hands back fresh objects too, and
    // the reconcile in usePanelState depends on that.
    return delay({ id: panelId, name: 'Climate', items: ITEMS.map((item) => ({ ...item })) });
  }

  setPower(_panelId: string, itemId: string, on: boolean): Promise<void> {
    const item = find(itemId);
    item.isOn = on;
    // A thermostat's mode follows its power, the way the real write path's
    // OnMode fallback makes it: the device reports "cool" or "off", never both.
    if (item.controlKind === 'Thermostat' && item.mode !== null) item.mode = on ? 'cool' : 'off';
    return delay(undefined);
  }

  setSetpoint(_panelId: string, itemId: string, valueF: number): Promise<void> {
    const item = find(itemId);
    if (item.controlKind !== 'Thermostat') throw new Error('A Switch control has no setpoint.');
    item.setpointF = snap(valueF, item.minF ?? 60, item.maxF ?? 85, item.stepF ?? 1);
    return delay(undefined);
  }
}
