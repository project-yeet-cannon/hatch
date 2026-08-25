import { useEffect, useMemo, useRef } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faXmark } from '@fortawesome/free-solid-svg-icons';
import type { PanelItemState, PanelSummary } from '../types';
import { usePanelState } from '../hooks/usePanelState';
import { useRoutineTaps, type RoutineTaps, type RoutineTapTarget } from '../hooks/useRoutineTaps';
import { clientLogger } from '../lib/clientLogger';
import { ACTIVITY_EVENTS } from '../lib/kioskLifecycle';
import { RoutineTile } from './RoutineTile';
import { SwitchControl } from './SwitchControl';
import { ThermostatControl } from './ThermostatControl';

/**
 * A Panel, full-screen over the dashboard — the sub-UI a tile opens onto.
 *
 * Shaped like GatherOverlay for the reasons that one is shaped that way: fixed
 * and mounted inside `.hf-page`, because the circadian palette is inline custom
 * properties on that element and an overlay anywhere else resolves none of
 * them; a 56px close target in the same corner, because that gesture is already
 * learned; and its own longer idle timeout, because standing at the wall
 * nudging a thermostat is a legitimate way to spend a minute.
 *
 * Shorter than Gather's 90s, though: this one has no text field to lose, and
 * what it holds open is a 5s poll rather than a list someone is reading.
 */
const OVERLAY_IDLE_MS = 60_000;

export function PanelOverlay({ panel, onClose }: { panel: PanelSummary; onClose: () => void }) {
  const { state, items, loadError, busy, errorFor, setPower, setSetpoint } = usePanelState(panel.id);

  // Held in a ref so the timer below survives the parent re-rendering, which it
  // does every 15s for the clock alone. An effect that re-ran on a new onClose
  // identity would re-arm the idle close forever and the wall would sit on the
  // panel until someone walked past.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | null = null;

    const arm = () => {
      if (timer !== null) clearTimeout(timer);
      timer = setTimeout(() => {
        clientLogger.info('Panel overlay idle; returning to the dashboard', { idleMs: OVERLAY_IDLE_MS });
        onCloseRef.current();
      }, OVERLAY_IDLE_MS);
    };

    arm();
    for (const event of ACTIVITY_EVENTS) {
      window.addEventListener(event, arm, { passive: true, capture: true });
    }
    return () => {
      if (timer !== null) clearTimeout(timer);
      for (const event of ACTIVITY_EVENTS) {
        window.removeEventListener(event, arm, { capture: true });
      }
    };
  }, []);

  // The routine items, in the shape the shared tap behaviour speaks. Memoised
  // because the hook reconciles its optimistic state against this array's
  // identity, and a fresh one every render would do that on every render
  // instead of on every poll.
  const routineTargets = useMemo<RoutineTapTarget[]>(
    () =>
      items
        .filter((item) => item.kind === 'Routine' && item.routineId !== null)
        .map((item) => ({
          id: item.routineId!,
          name: item.label ?? 'Routine',
          isToggle: item.isToggle ?? false,
          isActive: item.isActive,
        })),
    [items],
  );
  const taps = useRoutineTaps(routineTargets);

  return (
    <div className="hf-panel-overlay" role="dialog" aria-modal="true" aria-label={panel.name}>
      <div className="hf-panel-bar">
        {/* The summary's name until the first state fetch lands, so the title
            bar is never blank - the tile that was just tapped already said it. */}
        <span className="hf-panel-bar-title">{state?.name ?? panel.name}</span>
        <button type="button" className="hf-gather-icon-btn" onClick={onClose} aria-label="Close">
          <FontAwesomeIcon icon={faXmark} />
        </button>
      </div>

      <div className="hf-panel-scroll">
        {items.map((item) =>
          item.kind === 'Routine' ? (
            <RoutineItem key={item.id} item={item} targets={routineTargets} taps={taps} />
          ) : item.controlKind === 'Thermostat' ? (
            <ThermostatControl
              key={item.id}
              item={item}
              busy={busy(item.id)}
              error={errorFor(item.id)}
              onSetPower={(on) => setPower(item, on)}
              onSetSetpoint={(valueF) => setSetpoint(item, valueF)}
            />
          ) : (
            <SwitchControl
              key={item.id}
              item={item}
              busy={busy(item.id)}
              error={errorFor(item.id)}
              onSetPower={(on) => setPower(item, on)}
            />
          ),
        )}
        {/* A failed load leaves whatever was last fetched on screen rather than
            blanking the panel - a control a poll stale is a smaller lie than a
            control that vanished. With nothing fetched yet there is nothing to
            keep, and the reason is all there is to say. */}
        {state === null && loadError !== null && (
          <p className="hf-note" role="alert">
            Couldn’t load this panel — {loadError}
          </p>
        )}
        {state !== null && items.length === 0 && <p className="hf-panel-empty">Nothing on this panel yet.</p>}
      </div>
    </div>
  );
}

/**
 * A routine inside a panel: the same tile, the same trigger, the same
 * optimistic toggle as the dashboard row, in a card so it sits in the column
 * with the controls rather than beside them.
 */
function RoutineItem({
  item,
  targets,
  taps,
}: {
  item: PanelItemState;
  targets: RoutineTapTarget[];
  taps: RoutineTaps;
}) {
  const target = targets.find((candidate) => candidate.id === item.routineId);
  // Only reachable for a row written outside the API - the RoutineId cascade
  // means a deleted routine takes its item with it. Saying so beats a tile that
  // does nothing when tapped.
  if (!target) return <div className="hf-panel-card hf-panel-card-routine">{item.label ?? 'Routine (missing)'}</div>;

  return (
    <div className="hf-panel-card hf-panel-card-routine">
      <RoutineTile
        name={target.name}
        icon={item.icon}
        color={item.color}
        isToggle={target.isToggle}
        isActive={taps.isActive(target)}
        pending={taps.isPending(target)}
        error={taps.errorFor(target)}
        onTap={() => taps.tap(target)}
      />
    </div>
  );
}
