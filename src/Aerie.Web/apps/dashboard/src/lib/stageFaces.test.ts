import { describe, expect, it } from 'vitest';
import { stageFaces } from './stageFaces';
import type { CalendarDay } from '../types';

const busyDay: CalendarDay = {
  date: '2026-08-28',
  events: [
    {
      id: 'e1',
      calendarName: 'Family',
      color: null,
      title: 'Piano lesson',
      location: null,
      isAllDay: false,
      startsAt: '2026-08-28T17:00:00.000Z',
      endsAt: '2026-08-28T18:00:00.000Z',
    },
  ],
};
const emptyDay: CalendarDay = { date: '2026-08-28', events: [] };

describe('stageFaces', () => {
  it('gives both faces, photos first, when both have content', () => {
    expect(stageFaces(3, [busyDay])).toEqual(['photos', 'agenda']);
  });

  it('gives the lone face alone - nothing to swipe to', () => {
    expect(stageFaces(3, [emptyDay])).toEqual(['photos']);
    expect(stageFaces(0, [busyDay])).toEqual(['agenda']);
  });

  it('gives no stage at all when neither has content', () => {
    expect(stageFaces(0, [emptyDay])).toEqual([]);
    expect(stageFaces(0, [])).toEqual([]);
  });

  it('counts a busy tomorrow as agenda content even when today is empty', () => {
    // "Nothing today" over the tomorrow whisper is only worth rendering
    // because some other day isn't empty - the CalendarSection guard, shared.
    const tomorrow: CalendarDay = { ...busyDay, date: '2026-08-29' };
    expect(stageFaces(0, [emptyDay, tomorrow])).toEqual(['agenda']);
  });
});
