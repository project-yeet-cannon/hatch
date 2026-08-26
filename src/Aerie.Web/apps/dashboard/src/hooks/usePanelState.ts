import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getPanelSource } from '../dataSource';
import { clientLogger } from '../lib/clientLogger';
import type { PanelItemState, PanelState } from '../types';

/**
 * The live state behind an open Panel overlay, and the two writes its controls
 * make.
 *
 * Fast enough that a fan someone flipped from their phone shows up while you
 * are still standing there, and cheap because it is scoped to the one panel
 * that is open — which is the whole reason panel state is its own endpoint
 * rather than a field on the 60s dashboard snapshot.
 */
const POLL_INTERVAL_MS = 5_000;

/**
 * How long a tap defends itself against what the server says. Both halves of
 * the house pattern are here, for the reasons each already exists: an override
 * clears the moment a snapshot agrees with it (RoutinesSection), and expires on
 * its own if no snapshot ever does (GatherOverlay). Without the expiry, a
 * device that refused the command — or one Home Assistant accepted and never
 * actuated — would leave the wall claiming a state the house is not in until
 * someone closed the overlay.
 */
const OPTIMISTIC_TTL_MS = 30_000;

/**
 * How long the ⊖/⊕ buttons have to be quiet before the setpoint is dispatched.
 * Holding "+" from 68 to 78 is one decision, and it lands as one ledgered
 * SetTemperature rather than ten (docs/kiosk-architecture.md). The draft value
 * is shown immediately regardless — this delays the write, never the number.
 */
const SETPOINT_SETTLE_MS = 700;

interface Override<T> {
  value: T;
  at: number;
}

interface ItemOverride {
  isOn?: Override<boolean>;
  setpointF?: Override<number>;
}

export interface PanelStateHandle {
  /** Null until the first fetch lands. Carries the panel's name for the overlay's title bar. */
  state: PanelState | null;
  /** The panel's items with any outstanding optimistic overrides applied — what to render. */
  items: PanelItemState[];
  /** A failed *load*. A failed write is per-item; see errorFor. */
  loadError: string | null;
  busy(itemId: string): boolean;
  errorFor(itemId: string): string | null;
  setPower(item: PanelItemState, on: boolean): void;
  /** Shows the value at once and dispatches once the buttons go quiet. */
  setSetpoint(item: PanelItemState, valueF: number): void;
}

export function usePanelState(panelId: string): PanelStateHandle {
  const [source] = useState(getPanelSource);
  const [state, setState] = useState<PanelState | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [overrides, setOverrides] = useState<Record<string, ItemOverride>>({});
  const [busyIds, setBusyIds] = useState<string[]>([]);
  const [errors, setErrors] = useState<Record<string, string>>({});

  /** One settle timer per item, so two thermostats on one panel don't cancel each other. */
  const settleRef = useRef<Map<string, { timer: ReturnType<typeof setTimeout>; valueF: number }>>(new Map());

  const markBusy = useCallback((itemId: string, busy: boolean) => {
    setBusyIds((prev) => (busy ? (prev.includes(itemId) ? prev : [...prev, itemId]) : prev.filter((id) => id !== itemId)));
  }, []);

  /** A write that failed put the control back where the server has it — a switch showing "On" that never turned on is the one thing worse than a visible error. */
  const revert = useCallback((itemId: string, field: keyof ItemOverride, reason: string) => {
    setErrors((prev) => ({ ...prev, [itemId]: reason }));
    setOverrides((prev) => {
      const current = prev[itemId];
      if (!current || current[field] === undefined) return prev;
      const { [field]: _dropped, ...rest } = current;
      return { ...prev, [itemId]: rest };
    });
  }, []);

  const load = useCallback(async () => {
    try {
      const next = await source.getState(panelId);
      setState(next);
      setLoadError(null);

      // An override the snapshot agrees with has done its job; one the snapshot
      // still contradicts after the TTL has lost the argument.
      setOverrides((prev) => {
        const now = Date.now();
        let changed = false;
        const remaining: Record<string, ItemOverride> = {};
        for (const [itemId, override] of Object.entries(prev)) {
          // An item that vanished from the panel - an admin edited it while the
          // overlay was open - takes its overrides with it.
          const fresh = next.items.find((item) => item.id === itemId);
          const keepIsOn =
            override.isOn !== undefined &&
            fresh !== undefined &&
            fresh.isOn !== override.isOn.value &&
            now - override.isOn.at < OPTIMISTIC_TTL_MS;
          const keepSetpoint =
            override.setpointF !== undefined &&
            fresh !== undefined &&
            fresh.setpointF !== override.setpointF.value &&
            now - override.setpointF.at < OPTIMISTIC_TTL_MS;

          if (keepIsOn === (override.isOn !== undefined) && keepSetpoint === (override.setpointF !== undefined)) {
            // Nothing settled or expired; hold the same object so the render
            // that a 5s poll triggers isn't a new one every time.
            remaining[itemId] = override;
            continue;
          }
          changed = true;
          if (keepIsOn || keepSetpoint) {
            const kept: ItemOverride = {};
            if (keepIsOn) kept.isOn = override.isOn;
            if (keepSetpoint) kept.setpointF = override.setpointF;
            remaining[itemId] = kept;
          }
        }
        return changed ? remaining : prev;
      });
    } catch (err: unknown) {
      const reason = err instanceof Error ? err.message : String(err);
      clientLogger.error('Panel state load failed', { panelId, reason });
      setLoadError(reason);
    }
  }, [panelId, source]);

  useEffect(() => {
    void load();
    const poll = setInterval(() => void load(), POLL_INTERVAL_MS);
    return () => clearInterval(poll);
  }, [load]);

  const dispatchSetpoint = useCallback(
    (itemId: string, valueF: number) => {
      markBusy(itemId, true);
      source
        .setSetpoint(panelId, itemId, valueF)
        .then(() => {
          clientLogger.info('Panel setpoint set', { panelId, itemId, valueF });
          setErrors((prev) => {
            if (!(itemId in prev)) return prev;
            const { [itemId]: _cleared, ...rest } = prev;
            return rest;
          });
        })
        .catch((err: unknown) => {
          const reason = err instanceof Error ? err.message : String(err);
          clientLogger.error('Panel setpoint failed', { panelId, itemId, valueF, reason });
          revert(itemId, 'setpointF', reason);
        })
        .finally(() => markBusy(itemId, false));
    },
    [markBusy, panelId, revert, source],
  );

  // Closing the overlay a beat after the last ⊕ must not swallow the write, so
  // anything still settling is flushed on the way out. Held in a ref rather
  // than a dependency because this effect must run exactly on unmount.
  const dispatchRef = useRef(dispatchSetpoint);
  dispatchRef.current = dispatchSetpoint;
  useEffect(() => {
    const settling = settleRef.current;
    return () => {
      for (const [itemId, pending] of settling) {
        clearTimeout(pending.timer);
        dispatchRef.current(itemId, pending.valueF);
      }
      settling.clear();
    };
  }, []);

  const setPower = useCallback(
    (item: PanelItemState, on: boolean) => {
      setOverrides((prev) => ({ ...prev, [item.id]: { ...prev[item.id], isOn: { value: on, at: Date.now() } } }));
      markBusy(item.id, true);
      source
        .setPower(panelId, item.id, on)
        .then(() => {
          clientLogger.info('Panel power set', { panelId, itemId: item.id, label: item.label, on });
          setErrors((prev) => {
            if (!(item.id in prev)) return prev;
            const { [item.id]: _cleared, ...rest } = prev;
            return rest;
          });
        })
        .catch((err: unknown) => {
          const reason = err instanceof Error ? err.message : String(err);
          clientLogger.error('Panel power failed', { panelId, itemId: item.id, on, reason });
          revert(item.id, 'isOn', reason);
        })
        .finally(() => markBusy(item.id, false));
    },
    [markBusy, panelId, revert, source],
  );

  const setSetpoint = useCallback(
    (item: PanelItemState, valueF: number) => {
      setOverrides((prev) => ({ ...prev, [item.id]: { ...prev[item.id], setpointF: { value: valueF, at: Date.now() } } }));

      const settling = settleRef.current;
      const existing = settling.get(item.id);
      if (existing) clearTimeout(existing.timer);
      const timer = setTimeout(() => {
        settling.delete(item.id);
        dispatchSetpoint(item.id, valueF);
      }, SETPOINT_SETTLE_MS);
      settling.set(item.id, { timer, valueF });
    },
    [dispatchSetpoint],
  );

  const items = useMemo(() => {
    if (state === null) return [];
    return state.items.map((item) => {
      const override = overrides[item.id];
      if (!override) return item;
      return {
        ...item,
        isOn: override.isOn ? override.isOn.value : item.isOn,
        setpointF: override.setpointF ? override.setpointF.value : item.setpointF,
      };
    });
  }, [overrides, state]);

  return {
    state,
    items,
    loadError,
    busy: (itemId) => busyIds.includes(itemId),
    errorFor: (itemId) => errors[itemId] ?? null,
    setPower,
    setSetpoint,
  };
}
