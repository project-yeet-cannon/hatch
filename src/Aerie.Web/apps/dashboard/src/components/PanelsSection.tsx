import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import type { PanelSummary } from '../types';
import { clientLogger } from '../lib/clientLogger';
import { iconFor } from '../lib/icons';

/**
 * A tap-to-open button per Panel, in its own row below the cameras. Same tile
 * as a routine's — literally, the geometry rules in theme.css are shared —
 * because it belongs to the same band of the screen and the same gesture, while
 * sitting in its own row because tapping it opens a sub-UI rather than doing
 * something in the house.
 *
 * Nothing here talks to the network, and deliberately so: a panel's live
 * control state costs a query per bound channel, and gathering it for every
 * panel on the 60s dashboard poll — whether or not anyone opened one — is the
 * thing PanelSummary exists to avoid. The overlay fetches its own.
 */
export function PanelsSection({ panels, onOpen }: { panels: PanelSummary[]; onOpen: (panelId: string) => void }) {
  return (
    <div className="hf-panels">
      {panels.map((panel) => (
        <button
          key={panel.id}
          type="button"
          className="hf-panel-tile"
          onClick={() => {
            clientLogger.info('Panel opened', { panelId: panel.id, name: panel.name });
            onOpen(panel.id);
          }}
        >
          <span
            className="hf-panel-tile-circle"
            style={{ background: panel.color ? `color-mix(in srgb, ${panel.color} 16%, transparent)` : 'var(--line)' }}
          >
            <FontAwesomeIcon
              icon={iconFor(panel.icon)}
              className="hf-panel-tile-icon"
              style={{ color: panel.color ?? undefined }}
            />
          </span>
          <span className="hf-panel-tile-name">{panel.name}</span>
        </button>
      ))}
    </div>
  );
}
