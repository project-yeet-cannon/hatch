import type { CalendarDay } from '../types';

export type StageFace = 'photos' | 'agenda';

/**
 * Which faces the stage has, in swipe order - photos first, because the photo
 * frame is the wall's resting face and the agenda is the glance you swipe to.
 *
 * The existence matrix (docs/plans/dashboard-redesign.md, "The stage"):
 * both → two faces and dots; one → that face alone, no dots, nothing to
 * swipe; neither → no stage at all, which is the renders-nothing rule the
 * photo box and the agenda already followed separately, composed.
 *
 * The agenda face exists when *any* day in the window has events, not just
 * today - an empty today above a busy tomorrow renders "Nothing today" over
 * the whisper, which is only worth saying because some other day isn't empty.
 * The same guard the full CalendarSection uses.
 */
export function stageFaces(photoCount: number, calendar: CalendarDay[]): StageFace[] {
  const faces: StageFace[] = [];
  if (photoCount > 0) faces.push('photos');
  if (calendar.some((day) => day.events.length > 0)) faces.push('agenda');
  return faces;
}
