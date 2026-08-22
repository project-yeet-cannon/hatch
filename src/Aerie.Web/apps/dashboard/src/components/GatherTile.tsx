import type { GatherListSummary } from '../types';
import { DEFAULT_LIST_ICON, tintBackgroundOf } from '../lib/listColor';

/**
 * Gather on the dashboard: one button per list, showing what is still to get.
 * Read across the kitchen rather than at arm's length, so the count is the
 * biggest thing on it - "milk, eggs, and four more" is a decision you make from
 * the doorway, and opening the overlay is what you do once you've made it.
 *
 * Renders nothing when there are no lists. A household that hasn't made one
 * gets no dead tile, and the first run of a fresh deployment looks like the
 * feature simply isn't there yet - which it isn't.
 */
export function GatherTile({ lists, onOpen }: { lists: GatherListSummary[]; onOpen: (listId: string) => void }) {
  if (lists.length === 0) return null;

  return (
    <div className="hf-gather">
      {lists.map((list) => (
        <button key={list.id} type="button" className="hf-gather-tile" onClick={() => onOpen(list.id)}>
          <span className="hf-gather-tile-head">
            <span className="hf-gather-tile-icon" style={{ background: tintBackgroundOf(list.color) }} aria-hidden="true">
              {list.icon ?? DEFAULT_LIST_ICON}
            </span>
            <span className="hf-gather-tile-name">{list.name}</span>
          </span>
          <span className={`hf-gather-tile-count${list.openCount === 0 ? ' empty' : ''}`}>
            {list.openCount === 0 ? '—' : list.openCount}
          </span>
        </button>
      ))}
    </div>
  );
}
