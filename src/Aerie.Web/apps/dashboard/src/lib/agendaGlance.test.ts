import { describe, expect, it } from 'vitest';
import { AGENDA_GLANCE_MAX, glanceRows, tomorrowWhisper } from './agendaGlance';
import type { CalendarDay, CalendarEventSummary } from '../types';

const NOW = new Date('2026-08-28T16:00:00.000Z');
const TIME_ZONE = 'UTC';

let nextId = 0;
function event(startHourUtc: number, endHourUtc: number, isAllDay = false): CalendarEventSummary {
  nextId += 1;
  return {
    id: `e${nextId}`,
    calendarName: 'Family',
    color: null,
    title: `Event ${nextId}`,
    location: null,
    isAllDay,
    startsAt: new Date(Date.UTC(2026, 7, 28, startHourUtc)).toISOString(),
    endsAt: new Date(Date.UTC(2026, 7, 28, endHourUtc)).toISOString(),
  };
}

/** Server order: all-day first, then timed by start - what the API sends. */
function day(events: CalendarEventSummary[]): CalendarDay {
  return { date: '2026-08-28', events };
}

const titles = (rows: CalendarEventSummary[]) => rows.map((e) => e.title);

describe('glanceRows', () => {
  it('shows what is ahead, in order, under the cap', () => {
    const upcoming = [event(17, 18), event(19, 20)];
    const { events, overflowCount } = glanceRows(day(upcoming), NOW);
    expect(events).toEqual(upcoming);
    expect(overflowCount).toBe(0);
  });

  it('counts what is ahead but does not fit, and never drops it silently', () => {
    const upcoming = [event(17, 18), event(18, 19), event(19, 20), event(20, 21), event(21, 22)];
    const { events, overflowCount } = glanceRows(day(upcoming), NOW);
    expect(events).toEqual(upcoming.slice(0, AGENDA_GLANCE_MAX));
    expect(overflowCount).toBe(1);
  });

  it('keeps an in-progress event - it is the one thing happening right now', () => {
    const inProgress = event(15, 17);
    const { events } = glanceRows(day([inProgress, event(18, 19)]), NOW);
    expect(events[0]).toBe(inProgress);
  });

  it('treats an all-day event as relevant all day, never as past', () => {
    const allDay = event(0, 24, true);
    const { events } = glanceRows(day([allDay, event(9, 10), event(17, 18)]), NOW);
    // The morning event is past and only backfills; the all-day one is content.
    expect(titles(events)).toContain(allDay.title);
  });

  it('backfills a winding-down day with the most recent past, keeping chronology', () => {
    const past = [event(8, 9), event(10, 11), event(12, 13)];
    const upcoming = [event(17, 18), event(19, 20)];
    const { events, overflowCount } = glanceRows(day([...past, ...upcoming]), NOW);
    // Two upcoming + the two most recent past; the 8am one falls off the top.
    expect(titles(events)).toEqual(titles([past[1], past[2], ...upcoming]));
    expect(overflowCount).toBe(0);
  });

  it('shows a fully finished day as its most recent events, all faded, no overflow', () => {
    const past = [event(6, 7), event(8, 9), event(10, 11), event(12, 13), event(14, 15)];
    const { events, overflowCount } = glanceRows(day(past), NOW);
    expect(events).toEqual(past.slice(1));
    expect(overflowCount).toBe(0);
  });

  it('renders an empty or absent day as no rows', () => {
    expect(glanceRows(day([]), NOW).events).toEqual([]);
    expect(glanceRows(undefined, NOW).events).toEqual([]);
    expect(glanceRows(undefined, NOW).overflowCount).toBe(0);
  });
});

describe('tomorrowWhisper', () => {
  const tomorrow = (events: CalendarEventSummary[]): CalendarDay => ({ date: '2026-08-29', events });

  it('names the count and the first timed start', () => {
    expect(tomorrowWhisper(tomorrow([event(0, 24, true), event(8, 9), event(13, 14)]), TIME_ZONE)).toBe(
      'Tomorrow · 3 events · first at 8:00am',
    );
  });

  it('speaks singular for one event', () => {
    expect(tomorrowWhisper(tomorrow([event(8, 9)]), TIME_ZONE)).toBe('Tomorrow · 1 event · first at 8:00am');
  });

  it('drops the time when tomorrow is only all-day entries - there is no hour worth naming', () => {
    expect(tomorrowWhisper(tomorrow([event(0, 24, true)]), TIME_ZONE)).toBe('Tomorrow · 1 event');
  });

  it('says nothing for an empty or absent tomorrow', () => {
    expect(tomorrowWhisper(tomorrow([]), TIME_ZONE)).toBeNull();
    expect(tomorrowWhisper(undefined, TIME_ZONE)).toBeNull();
  });
});
