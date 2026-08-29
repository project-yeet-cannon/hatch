import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faXmark } from '@fortawesome/free-solid-svg-icons';
import type { LoggedEntry } from '../lib/clientLogger';
import { readLoadedVersion } from '../lib/appVersion';
import { formatShortTime } from '../lib/format';

interface HealthModalProps {
  entries: readonly LoggedEntry[];
  timeZone: string;
  onClose: () => void;
}

/**
 * What the dot is about. Same overlay idiom as GatherOverlay - fixed, and a
 * child of .hf-page rather than portalled, because the circadian palette is
 * inline custom properties on that element and an overlay mounted anywhere else
 * would resolve none of them.
 *
 * Newest first: on a wall, the question is always "what is wrong now", and the
 * older lines are context for it rather than a history to read forward.
 *
 * The footer carries the build and the device id so a photograph of this screen
 * is a diagnosis - these tablets are not somewhere anyone can open a console.
 */
export function HealthModal({ entries, timeZone, onClose }: HealthModalProps) {
  const newestFirst = [...entries].reverse();

  return (
    <div className="hf-health-overlay" role="dialog" aria-modal="true" aria-label="Problems">
      <div className="hf-health-bar">
        <span className="hf-health-title">Problems</span>
        <button type="button" className="hf-health-close" onClick={onClose} aria-label="Close">
          <FontAwesomeIcon icon={faXmark} />
        </button>
      </div>

      <div className="hf-health-scroll">
        {newestFirst.length === 0 ? (
          <div className="hf-health-empty">Nothing has gone wrong since this page loaded.</div>
        ) : (
          newestFirst.map((entry) => (
            <div key={entry.id} className={`hf-health-row ${entry.level}`}>
              <span className="hf-health-when">{formatShortTime(entry.timestamp, timeZone)}</span>
              <span className="hf-health-message">{entry.message}</span>
              {describe(entry) && <span className="hf-health-detail">{describe(entry)}</span>}
            </div>
          ))
        )}
      </div>

      <div className="hf-health-foot">
        <button type="button" className="hf-health-reload" onClick={() => location.reload()}>
          Reload the dashboard
        </button>
        <span className="hf-health-build">{readLoadedVersion() || 'dev build'}</span>
      </div>
    </div>
  );
}

/**
 * The one line of detail worth the width. The metadata object carries whatever
 * each call site passed - device blobs included - and dumping it would fill the
 * screen with things nobody standing at a tablet can act on, so this picks the
 * few fields that say *where* rather than *what*.
 */
function describe(entry: LoggedEntry): string | null {
  const detail = entry.detail;
  const parts: string[] = [];

  if (typeof detail.status === 'number') parts.push(`HTTP ${detail.status}`);
  if (typeof detail.path === 'string') parts.push(detail.path);
  if (typeof detail.reason === 'string') parts.push(detail.reason);
  if (typeof detail.source === 'string' && detail.source !== 'console.error') parts.push(detail.source);
  if (typeof detail.line === 'number') parts.push(`line ${detail.line}`);
  if (typeof detail.sinceLast === 'number') parts.push(`+${detail.sinceLast} more since`);

  return parts.length > 0 ? parts.join(' · ') : null;
}
