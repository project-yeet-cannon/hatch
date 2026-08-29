import { Fragment } from 'react';
import type { CSSProperties } from 'react';
import type { CalendarDay, CalendarEventSummary } from '../types';
import { formatAgendaDate, formatAgendaTime } from '../lib/format';
import { calendarDateInZone } from '../lib/timezone';

/**
 * The family agenda: one heading per day in the window, then each event as a
 * calendar block - a rounded card tinted with its calendar's color, a solid
 * rail down its left edge, and the times in a narrow gutter beside it.
 *
 * Three things carry the time-of-day reading, all of them only on today:
 *
 * 1. A now line - an accent rule with a dot, sitting between the last event
 *    that has started and the first that hasn't. It is the same affordance a
 *    day-view calendar uses, and it is what makes the rest of the column
 *    legible at a glance from across the room.
 * 2. An in-progress block gets a ring in its own calendar color, so "the thing
 *    happening right now" is findable without reading a single time.
 * 3. Anything already over is faded rather than dropped. Dropping would make
 *    the column jump as the day passes and would erase the evidence that the
 *    morning was busy; fading keeps the day's shape while pushing it behind
 *    what's still ahead.
 *
 * `now` is the app's shared 15s tick (App.tsx) rather than a timer of this
 * component's own - one clock for the page keeps the now line and the header
 * clock from disagreeing by up to a tick.
 */
export function CalendarSection({
  calendar,
  timeZone,
  now,
}: {
  calendar: CalendarDay[];
  timeZone: string;
  now: Date;
}) {
  const today = calendarDateInZone(now, timeZone);

  return (
    <section className="hf-cal" aria-label="Family agenda">
      {calendar.map((day) => {
        const isToday = day.date === today;
        // Where the now line goes: above the first event still to come. All-day
        // events sort ahead of timed ones and have no start worth comparing, so
        // they are never what the line lands on; when everything timed has
        // started, it falls to the bottom of the day.
        const nowIndex = isToday
          ? day.events.findIndex((event) => !event.isAllDay && new Date(event.startsAt) > now)
          : -1;
        const nowLineAt = isToday && day.events.length > 0 ? (nowIndex === -1 ? day.events.length : nowIndex) : -1;

        return (
          <div className="hf-cal-day" key={day.date}>
            {/* The generalized section header (.hf-sec-head, theme.css) - the
                agenda is where the idiom was born, and it keeps using it. */}
            <div className="hf-sec-head">
              <span className="hf-sec-label">{isToday ? 'Today' : formatAgendaDate(day.date)}</span>
              <span className="hf-sec-rule" aria-hidden="true" />
            </div>
            {day.events.length === 0 ? (
              <div className="hf-cal-empty">Nothing scheduled</div>
            ) : (
              <div className="hf-cal-list">
                {day.events.map((event, i) => (
                  <Fragment key={event.id}>
                    {i === nowLineAt && <NowLine now={now} timeZone={timeZone} />}
                    <EventBlock event={event} timeZone={timeZone} now={now} />
                  </Fragment>
                ))}
                {nowLineAt === day.events.length && <NowLine now={now} timeZone={timeZone} />}
              </div>
            )}
          </div>
        );
      })}
    </section>
  );
}

function NowLine({ now, timeZone }: { now: Date; timeZone: string }) {
  return (
    <div className="hf-cal-now">
      <span className="hf-cal-now-time">{formatAgendaTime(now.toISOString(), timeZone)}</span>
      <span className="hf-cal-now-rule" aria-hidden="true" />
    </div>
  );
}

/**
 * One event row - time gutter plus tinted block. Exported for the stage's
 * agenda face (components/Stage.tsx), which renders the same object the full
 * agenda does so the two can't drift; the now-line stays private to this
 * section, deliberately - the glance face carries "right now" with the
 * in-progress ring alone.
 */
export function EventBlock({
  event,
  timeZone,
  now,
}: {
  event: CalendarEventSummary;
  timeZone: string;
  now: Date;
}) {
  const ends = new Date(event.endsAt).getTime();
  const starts = new Date(event.startsAt).getTime();
  // An all-day event is neither over nor "happening right now" in any useful
  // sense - it applies to the whole day, so it stays at full weight and never
  // takes the ring that marks the one thing currently underway.
  const isPast = !event.isAllDay && ends <= now.getTime();
  const isNow = !event.isAllDay && starts <= now.getTime() && ends > now.getTime();
  const meta = [event.location, event.calendarName].filter(Boolean).join(' · ');

  return (
    <div
      className={`hf-cal-row${isPast ? ' past' : ''}${event.isAllDay ? ' allday' : ''}`}
      style={{ '--cal-color': event.color ?? 'var(--muted)' } as CSSProperties}
    >
      <span className="hf-cal-time">
        {event.isAllDay ? (
          <span className="hf-cal-allday">All day</span>
        ) : (
          <>
            <span className="hf-cal-start">{formatAgendaTime(event.startsAt, timeZone)}</span>
            <span className="hf-cal-end">{formatAgendaTime(event.endsAt, timeZone)}</span>
          </>
        )}
      </span>
      <span className={`hf-cal-block${isNow ? ' now' : ''}`}>
        <span className="hf-cal-title">{event.title}</span>
        {meta && <span className="hf-cal-meta">{meta}</span>}
      </span>
    </div>
  );
}
