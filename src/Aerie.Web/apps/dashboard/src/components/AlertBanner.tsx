import type { CSSProperties } from 'react';
import type { HazardAlert, HazardSeverity } from '../types';
import { formatShortTime } from '../lib/format';

/**
 * Outdoor hazards: one card per active alert, in the same shape as everything
 * else in the column - card radius, a solid rail in the severity color, and
 * the rest of the card that color mixed into the current circadian --card.
 *
 * That mix is the point. A fixed red banner would be the brightest thing in
 * the house at 3am; mixing into --card means the hue survives and the
 * luminance follows whatever phase the page is in, without this component
 * knowing which phase that is.
 *
 * Severity is a ladder of presence rather than a change of hue alone - an
 * Extreme alert takes a wider rail, a heavier tint and a larger title, so it
 * separates from an advisory at across-the-room distance, where the difference
 * between orange and amber does not survive the trip.
 *
 * Renders nothing when there is nothing to say, which is most days.
 */
export function AlertBanner({ alerts, timeZone }: { alerts: HazardAlert[]; timeZone: string }) {
  if (alerts.length === 0) return null;

  return (
    <div className="hf-alerts" role="status">
      {alerts.map((alert) => (
        <div
          key={alert.id}
          className={`hf-alert sev-${alert.severity.toLowerCase()}`}
          style={{ '--sev-color': severityColor(alert.severity) } as CSSProperties}
        >
          <span className="hf-alert-title">{alert.title}</span>
          {alert.detail && <span className="hf-alert-detail">{alert.detail}</span>}
          {/* An air quality peak has a startsAt and no endsAt; a weather alert
              in effect has both, and its end is the more useful of the two. */}
          {alert.endsAt ? (
            <span className="hf-alert-when">until {formatShortTime(alert.endsAt, timeZone)}</span>
          ) : alert.startsAt ? (
            <span className="hf-alert-when">by {formatShortTime(alert.startsAt, timeZone)}</span>
          ) : null}
        </div>
      ))}
    </div>
  );
}

/**
 * Severity to a hue. Hardcoded rather than drawn from theme/tokens.ts because
 * these are the only colors on the page that must mean the same thing at every
 * hour - a warning that shifted hue with the circadian phase would be reporting
 * the time of day, not the weather. Only their luminance moves, and that
 * happens in .hf-alert's color-mix against --card.
 */
function severityColor(severity: HazardSeverity): string {
  switch (severity) {
    case 'Extreme':
      return '#d14343';
    case 'Severe':
      return '#e07a3f';
    case 'Moderate':
      return '#d9a441';
    default:
      return 'var(--line)';
  }
}
