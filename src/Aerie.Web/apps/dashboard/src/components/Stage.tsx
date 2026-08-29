import { useEffect, useRef, useState } from 'react';
import type { CalendarDay, CarouselPhoto, PhotoSource } from '../types';
import { stageFaces } from '../lib/stageFaces';
import { glanceRows, tomorrowWhisper } from '../lib/agendaGlance';
import { calendarDateInZone } from '../lib/timezone';
import { PhotoCarousel } from './PhotoCarousel';
import { EventBlock } from './CalendarSection';

/**
 * The stage: one photo-frame-shaped region holding the two things that share
 * the fold's big space - the photo carousel as the resting face, and a glance
 * at today's agenda one horizontal swipe away
 * (docs/plans/dashboard-redesign.md, "The stage").
 *
 * The pager is a scroll-snap track, not gesture code: `touch-action: pan-x
 * pan-y` on the track means a horizontal drag flips faces while a vertical
 * drag keeps scrolling the page - the stage never traps the page gesture.
 * Tapping a dot flips too. Renders nothing at all when neither face has
 * content, which is the renders-nothing rule both features already followed
 * separately, composed.
 *
 * The idle reset scrolls the track home *imperatively* rather than by
 * remount: remounting would cut a photo mid-dissolve and restart the deck,
 * and a wall that visibly does something on its own timer is the bug the
 * calm rule exists to prevent. The photo carousel keeps its own state across
 * resets exactly as it did before the stage existed.
 */
export function Stage({
  photos,
  photoSource,
  calendar,
  timeZone,
  now,
  resetToken,
}: {
  photos: CarouselPhoto[];
  photoSource: PhotoSource;
  calendar: CalendarDay[];
  timeZone: string;
  now: Date;
  resetToken?: number;
}) {
  const faces = stageFaces(photos.length, calendar);
  const trackRef = useRef<HTMLDivElement | null>(null);
  const [activeIndex, setActiveIndex] = useState(0);

  // Which face is under the viewport, from scroll position - drives the dots
  // and their photo-vs-card coloring. Rounded, so it settles correctly from
  // either direction and mid-drag reads as the nearer face.
  const onScroll = () => {
    const track = trackRef.current;
    if (track === null || track.clientWidth === 0) return;
    setActiveIndex(Math.round(track.scrollLeft / track.clientWidth));
  };

  // The idle reset lands the stage back on photos, instantly - the same
  // moment the climate card snaps to Outside and the page scrolls home, so
  // the whole wall arrives at rest in one motion, not a choreography.
  useEffect(() => {
    trackRef.current?.scrollTo({ left: 0 });
    setActiveIndex(0);
  }, [resetToken]);

  if (faces.length === 0) return null;

  const showDots = faces.length > 1;
  const scrollToFace = (index: number) => {
    const track = trackRef.current;
    if (track === null) return;
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    track.scrollTo({ left: index * track.clientWidth, behavior: reduced ? 'auto' : 'smooth' });
  };

  return (
    <div className="hf-stage">
      <div className="hf-stage-track" ref={trackRef} onScroll={onScroll}>
        {faces.map((face) => (
          <div className="hf-stage-face" key={face}>
            {face === 'photos' ? (
              <PhotoCarousel photos={photos} source={photoSource} timeZone={timeZone} />
            ) : (
              <AgendaFace calendar={calendar} timeZone={timeZone} now={now} />
            )}
          </div>
        ))}
      </div>
      {showDots && (
        <div className={`hf-stage-dots${faces[activeIndex] === 'photos' ? ' on-photo' : ''}`}>
          {faces.map((face, index) => (
            <button
              key={face}
              type="button"
              className={`hf-stage-dot${index === activeIndex ? ' active' : ''}`}
              aria-label={face === 'photos' ? 'Show photos' : 'Show today’s agenda'}
              onClick={() => scrollToFace(index)}
            />
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Today at a glance: a handful of rows in the full agenda's own idiom, capped
 * rather than scrolled - a nested scroller inside an x-snap track inside a
 * y-snap page is gesture soup, and a glance face shouldn't hold more than a
 * glance anyway. What didn't fit is counted, and tomorrow gets one whispered
 * line so it is never invisible.
 */
function AgendaFace({ calendar, timeZone, now }: { calendar: CalendarDay[]; timeZone: string; now: Date }) {
  const today = calendarDateInZone(now, timeZone);
  const todayDay = calendar.find((day) => day.date === today);
  // "Tomorrow" is the next day in the window after today, not blindly
  // calendar[1] - a window that starts on a past date must not shift the label.
  const tomorrowDay = calendar.find((day) => day.date > today);

  const { events, overflowCount } = glanceRows(todayDay, now);
  const whisper = tomorrowWhisper(tomorrowDay, timeZone);

  return (
    <div className="hf-stage-agenda">
      <div className="hf-sec-head">
        <span className="hf-sec-label">Today</span>
        <span className="hf-sec-rule" aria-hidden="true" />
      </div>
      {events.length === 0 ? (
        <div className="hf-stage-agenda-empty">Nothing today</div>
      ) : (
        <div className="hf-cal-list">
          {events.map((event) => (
            <EventBlock key={event.id} event={event} timeZone={timeZone} now={now} />
          ))}
        </div>
      )}
      {overflowCount > 0 && (
        <div className="hf-stage-agenda-more">
          +{overflowCount} more {overflowCount === 1 ? 'event' : 'events'} today
        </div>
      )}
      {whisper !== null && <div className="hf-stage-agenda-whisper">{whisper}</div>}
    </div>
  );
}
