import type { CalendarDay } from '../types';
import { formatAgendaDate, formatShortTime } from '../lib/format';
import { calendarDateInZone } from '../lib/timezone';

/**
 * The family agenda: one heading per day in the window, then each event as a
 * colored dot, a time, and a title.
 *
 * Provisional by design (docs/plans/kiosk.md phase A6) - it exists to prove
 * the data arrives, and phase C1 owns what it should actually look like and
 * where it belongs on the wall. Inline styles rather than theme.css classes
 * for the same reason: nothing here is worth naming until C1 decides the
 * layout.
 */
export function CalendarSection({ calendar, timeZone }: { calendar: CalendarDay[]; timeZone: string }) {
  const today = calendarDateInZone(new Date(), timeZone);

  return (
    <div style={{ display: 'grid', gap: '0.75rem', margin: '1rem 0' }}>
      {calendar.map((day) => (
        <div key={day.date}>
          <div style={{ opacity: 0.7, fontSize: '0.8rem', textTransform: 'uppercase', letterSpacing: '0.05em' }}>
            {day.date === today ? 'Today' : formatAgendaDate(day.date)}
          </div>
          {day.events.length === 0 ? (
            <div style={{ opacity: 0.5 }}>Nothing scheduled</div>
          ) : (
            day.events.map((event) => (
              <div key={event.id} style={{ display: 'flex', alignItems: 'baseline', gap: '0.5rem' }}>
                <span
                  aria-hidden="true"
                  style={{
                    width: '0.5rem',
                    height: '0.5rem',
                    borderRadius: '50%',
                    flex: '0 0 auto',
                    background: event.color ?? 'var(--line)',
                  }}
                />
                <span style={{ opacity: 0.7, minWidth: '4.5rem' }}>
                  {event.isAllDay ? 'All day' : formatShortTime(event.startsAt, timeZone)}
                </span>
                <span>{event.title}</span>
              </div>
            ))
          )}
        </div>
      ))}
    </div>
  );
}
