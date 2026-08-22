import type { HazardAlert, HazardSeverity } from '../types';
import { formatShortTime } from '../lib/format';

/**
 * Outdoor hazards: one line per active alert, colored by severity.
 *
 * Provisional by design (docs/plans/kiosk.md phase B4) - it exists to prove the
 * data arrives, and phase C1 owns what a warning should actually look like from
 * across the room and how it behaves under the circadian theme, which is the
 * part that matters most here: a red banner that ignores the night palette
 * becomes the brightest thing in the house at 3am.
 *
 * Renders nothing when there is nothing to say, which is most days - and is why
 * this can sit anywhere in the layout until C1 decides where.
 */
export function AlertBanner({ alerts, timeZone }: { alerts: HazardAlert[]; timeZone: string }) {
  if (alerts.length === 0) return null;

  return (
    <div style={{ display: 'grid', gap: '0.5rem', margin: '1rem 0' }} role="status">
      {alerts.map((alert) => (
        <div
          key={alert.id}
          style={{
            display: 'flex',
            alignItems: 'baseline',
            gap: '0.5rem',
            padding: '0.5rem 0.75rem',
            borderLeft: `3px solid ${severityColor(alert.severity)}`,
            background: 'color-mix(in srgb, currentColor 6%, transparent)',
          }}
        >
          <span style={{ fontWeight: 600 }}>{alert.title}</span>
          {alert.detail && <span style={{ opacity: 0.7 }}>{alert.detail}</span>}
          {/* An air quality peak has a startsAt and no endsAt; a weather alert
              in effect has both, and its end is the more useful of the two. */}
          {alert.endsAt ? (
            <span style={{ opacity: 0.5, marginLeft: 'auto' }}>until {formatShortTime(alert.endsAt, timeZone)}</span>
          ) : alert.startsAt ? (
            <span style={{ opacity: 0.5, marginLeft: 'auto' }}>by {formatShortTime(alert.startsAt, timeZone)}</span>
          ) : null}
        </div>
      ))}
    </div>
  );
}

/**
 * Severity to a color, provisionally. Hardcoded hexes rather than theme tokens
 * because C1 owns this decision entirely - including whether color is the right
 * carrier at all - and inventing token names now would only mean renaming them
 * then.
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
