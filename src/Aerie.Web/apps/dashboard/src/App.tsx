import { useCallback, useEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import type { DashboardData } from './types';
import { getDashboardDataSource } from './dataSource';
import { DEFAULT_TIME_ZONE } from './config';
import { formatClockParts, formatMonthDay, formatWeekday } from './lib/format';
import { useCircadianTheme } from './hooks/useCircadianTheme';
import { ZoneCard } from './components/ZoneCard';
import { ClimateCard } from './components/ClimateCard';
import { partitionZones } from './lib/leadZones';
import { RoutinesSection } from './components/RoutinesSection';
import { CamerasSection } from './components/CamerasSection';
import { PanelsSection } from './components/PanelsSection';
import { CalendarSection } from './components/CalendarSection';
import { PhotoCarousel } from './components/PhotoCarousel';
import { AlertBanner } from './components/AlertBanner';
import { DashboardSkeleton } from './components/DashboardSkeleton';
import { GatherTile } from './components/GatherTile';
import { GatherOverlay } from './components/GatherOverlay';
import { CameraFeedModal } from './components/CameraFeedModal';
import { PanelOverlay } from './components/PanelOverlay';
import { HealthDot } from './components/HealthDot';
import { HealthModal } from './components/HealthModal';
import { clientLogger } from './lib/clientLogger';
import { useKioskLifecycle } from './hooks/useKioskLifecycle';
import { useGatherLists } from './hooks/useGatherLists';
import { usePhotoCarousel } from './hooks/usePhotoCarousel';
import { useMotionEvents } from './hooks/useMotionEvents';
import { useHealthSignal } from './hooks/useHealthSignal';
import { createSkewWatcher } from './lib/clockSkew';
import { isLeaving } from './lib/signIn';

const REFRESH_INTERVAL_MS = 60_000;

/** The section-header idiom (.hf-sec-head, theme.css) - every named section on page two introduces itself with this and nothing else. */
function SectionHead({ label }: { label: string }) {
  return (
    <div className="hf-sec-head">
      <span className="hf-sec-label">{label}</span>
      <span className="hf-sec-rule" aria-hidden="true" />
    </div>
  );
}

export function App() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());
  // Device-clock reading of when the snapshot in `data` arrived. Only ever
  // subtracted from another device-clock reading, so a tablet whose own clock
  // is wrong cannot skew it - see nowOnServerClock below.
  const [snapshotReceivedAt, setSnapshotReceivedAt] = useState<number | null>(null);
  // null when the overlay is closed; the id of the list it is showing otherwise.
  const [gatherListId, setGatherListId] = useState<string | null>(null);
  // Likewise for Panels - null when closed, the opened panel's id otherwise.
  const [panelId, setPanelId] = useState<string | null>(null);
  const { lists: gatherLists, refresh: refreshGather } = useGatherLists();
  // Its own data path, like Gather's: a dashboard poll that fails should not
  // take the photo frame down with it.
  const { photos, source: photoSource } = usePhotoCarousel();
  // resetToken bumps ~30s after the last touch; see hooks/useKioskLifecycle.ts
  // and lib/kioskIdleTimings.ts. The hook also owns the reload-on-new-deploy
  // side of the kiosk's lifecycle, which needs nothing from this component. Both
  // are suspended while Gather is open - a reset would take a half-typed item
  // with it, and a reload would take the whole page.
  const gatherOpen = gatherListId !== null;
  // The panel the overlay is showing, from the snapshot that put its tile on
  // screen. A poll that drops the panel - an admin un-included it while someone
  // was standing there - closes the overlay by the same expression.
  const openPanel = panelId === null ? undefined : data?.panels.find((panel) => panel.id === panelId);
  // The camera a motion event is asking the wall to show, if any - see
  // hooks/useMotionEvents.ts and docs/camera-devices-architecture.md.
  const { cameraDeviceId: motionCameraId, dismiss: dismissMotion } = useMotionEvents();
  // The camera someone asked for by tapping its button, which wins
  // over motion for as long as it is open: a person standing at the tablet
  // watching the driveway should not be shoved onto the back door because a
  // branch moved. Motion resumes on close if it is still going.
  const [manualCameraId, setManualCameraId] = useState<string | null>(null);
  const cameraDeviceId = manualCameraId ?? motionCameraId;
  // How loud to be about the app's own failures. Fed by every clientLogger.error
  // in the app for free, and faded by the poll below - see lib/healthSignal.ts.
  const { level: healthLevel, entries: healthEntries, notePollSuccess } = useHealthSignal();
  const [healthOpen, setHealthOpen] = useState(false);
  // The hold follows what is on screen, not what is available. A motion-opened
  // feed suspends the lifecycle for the same reason Gather does - an idle reset
  // or a deploy reload would drop a live feed while someone is standing there
  // watching who is at the door - but a feed someone opened by hand deliberately
  // does not, so walking away from it closes it, and with it the relay's
  // connection and the camera's. A panel holds for Gather's reason rather than
  // the camera's: a reset or a deploy reload landing between the last "+" and
  // the settled write would drop that write, and the overlay runs its own
  // longer idle timer for as long as someone is standing there.
  // The health modal holds for Gather's reason: an idle reset or a deploy
  // reload landing while someone reads an error list takes the evidence away
  // mid-read, and re-reading it is not possible - a reload empties the buffer.
  const { resetToken } = useKioskLifecycle(
    gatherOpen || healthOpen || openPanel !== undefined || (manualCameraId === null && motionCameraId !== null),
  );

  // The idle rung for a hand-opened feed: the same reset that collapses the
  // cards closes it. Any touch anywhere restarts that timer, this overlay
  // included.
  useEffect(() => {
    setManualCameraId(null);
  }, [resetToken]);

  // One ✕, both halves. Clearing the manual selection alone would leave a
  // camera that is *also* in motion on screen under motion's ownership - a
  // close button that visibly does nothing - and dismissing alone would leave a
  // hand-opened feed up. Dismissal is a no-op on a device that isn't in motion,
  // which is the ordinary manual case.
  const closeCamera = useCallback(() => {
    setManualCameraId(null);
    if (cameraDeviceId !== null) dismissMotion(cameraDeviceId);
  }, [cameraDeviceId, dismissMotion]);

  const closeGather = useCallback(() => {
    setGatherListId(null);
    // The tile's counts are known to be wrong the instant this closes; waiting
    // out the poll would show someone the opposite of what they just did.
    refreshGather();
  }, [refreshGather]);

  useEffect(() => {
    clientLogger.info('App mounted, starting dashboard data source');
    const source = getDashboardDataSource();
    // Per mount rather than module-level, so a remount re-arms it.
    const skewWatcher = createSkewWatcher();
    let cancelled = false;
    let firstLoad = true;

    const load = () => {
      // Nothing to do for a page that is on its way to sign in - and if that
      // navigation cannot complete, this is what stops the poll leaking a
      // never-settling promise a minute forever. See lib/signIn.ts.
      if (isLeaving()) return;
      source
        .getDashboardData()
        .then((snapshot) => {
          if (cancelled) return;
          clientLogger.info(firstLoad ? 'Initial dashboard data loaded' : 'Dashboard data refreshed', {
            zoneCount: snapshot.zones.length,
          });
          firstLoad = false;
          setData(snapshot);
          setSnapshotReceivedAt(Date.now());
          setError(null);
          notePollSuccess();

          // The device clock is the floor the fallback screen, the native error
          // screen and the header all stand on, and a tablet that has been
          // offline can drift. Warn rather than error - a wrong clock is worth
          // seeing in the health modal, but it is not an outage and should not
          // raise the dot. Deliberately not corrected: a wall that silently
          // disagrees with the phone in your hand is the bug, and papering over
          // it removes its only visible symptom.
          const skewMs = skewWatcher.note(Date.now(), snapshot.generatedAt);
          if (skewMs !== null) {
            clientLogger.warn('Device clock disagrees with the server', {
              skewMinutes: Math.round(skewMs / 60_000),
              deviceNow: new Date().toISOString(),
              serverNow: snapshot.generatedAt,
            });
          }
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          const message = err instanceof Error ? err.message : String(err);
          clientLogger.error('Dashboard data load failed', { firstLoad, reason: message });
          setError(message);
        });
    };

    load();
    const refresh = setInterval(load, REFRESH_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(refresh);
    };
    // notePollSuccess is stable across renders (useCallback with no deps), so
    // this stays a mount-once effect and the poll is not restarted per render.
  }, [notePollSuccess]);

  useEffect(() => {
    const tick = setInterval(() => setNow(new Date()), 15_000);
    return () => clearInterval(tick);
  }, []);

  // Before the first snapshot arrives we don't know the house's timezone yet,
  // so fall back to the configured default — it's only used for a couple of
  // seconds' worth of header rendering and gets replaced once `data` loads.
  const timeZone = data?.timezone ?? DEFAULT_TIME_ZONE;
  /**
   * Now, expressed on the server's clock: the snapshot's own `generatedAt`
   * advanced by how long this page has been holding it.
   *
   * The cards measure a reading's age against this rather than against
   * `generatedAt` itself, and the difference only shows up during an outage -
   * which is the only time it matters. A frozen `generatedAt` freezes the age
   * with it, so a wall whose API died an hour ago would keep reporting every
   * bar as fresh: `generatedAt - currentAsOf` is a constant once both stop
   * moving. Advancing it makes each bar walk fresh -> stale -> no data on its
   * own while the poll keeps failing, which is the truth.
   *
   * Both terms of the elapsed subtraction come from the device clock and both
   * timestamps in the comparison from the server's, so neither clock's drift
   * leaks into the other. Re-derived on the 15s tick below, which is what
   * drives the cards through the ladder.
   */
  const nowOnServerClock =
    data === null || snapshotReceivedAt === null
      ? undefined
      : new Date(Date.parse(data.generatedAt) + (now.getTime() - snapshotReceivedAt)).toISOString();
  const theme = useCircadianTheme(now, data?.sunEvents);
  const clock = formatClockParts(now, timeZone);
  // The snapshot's row for whichever camera is on screen, if it has one. A
  // motion event can arrive while the dashboard poll is failing, so this is
  // allowed to be missing and the modal falls back to fetching the name.
  const shownCamera = cameraDeviceId === null ? undefined : data?.cameras.find((camera) => camera.id === cameraDeviceId);
  // The climate card's tabs and the "More rooms" rows - see lib/leadZones.ts.
  const zonesPartition = data === null ? null : partitionZones(data.zones);
  const calendarHasContent = data !== null && data.calendar.some((day) => day.events.length > 0);
  // Whether page two exists at all. No content means no .hf-p2, no snap point
  // and no swipe hint - a fresh deployment with nothing configured is a
  // one-page wall, which is the truth. Gather counts on its own because it
  // rides its own data path and can have lists while the snapshot is down.
  // The agenda is deliberately not in this list any more: it lives on page one
  // now, so a house whose only content is a calendar is a one-page wall.
  const hasPageTwo =
    gatherLists.length > 0 ||
    (data !== null &&
      (data.routines.length > 0 ||
        data.cameras.length > 0 ||
        data.panels.length > 0 ||
        (zonesPartition?.rest.length ?? 0) > 0));
  const pageTwoRef = useRef<HTMLDivElement | null>(null);
  const scrollToPageTwo = useCallback(() => {
    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    pageTwoRef.current?.scrollIntoView({ behavior: reduced ? 'auto' : 'smooth' });
  }, []);

  return (
    <div className="hf-page" style={theme.style as CSSProperties}>
      {/* The two polarity inversions a day, covered. See useCircadianTheme.ts
          for why they are a cut to dark rather than a fade. aria-hidden and
          pointer-events:none: it is a lighting effect, not a scrim - a tap
          during it still reaches whatever is underneath. */}
      <div
        className="hf-veil"
        aria-hidden="true"
        style={{ opacity: theme.veilOpacity, transitionDuration: `${theme.veilDurationMs}ms` }}
      />
      {/* Fixed to the viewport rather than placed in the header flow, so it
          stays put when the column is scrolled down a list. Renders nothing at
          all when the wall is healthy, which is nearly always. */}
      <HealthDot level={healthLevel} count={healthEntries.length} onOpen={() => setHealthOpen(true)} />
      <div className="hfdev">
        {/* Page one: the wall at rest, as one column - climate, agenda,
            photos - which on a day with events runs past the fold rather than
            hiding one of the three behind a gesture. Page two is below - the
            document itself is the pager, through the scroll-snap rules on
            html/.hf-p1/.hf-p2 in theme.css, so the lifecycle's scroll
            presence signal and the idle reset's scrollTo(0,0) keep working
            untouched (docs/plans/dashboard-redesign.md). */}
        <div className="hf-p1">
          <div className="hf-head">
            {/* The date comes from `now`, never from the snapshot. A snapshot
                that went stale before midnight and a device clock that did not
                would otherwise leave the wall reading "Thursday 3rd" above
                "12:20 AM" on Friday - the header would be reporting the age of
                the data while looking like it was reporting the date. How old
                the snapshot is belongs to the health dot, not the calendar. */}
            <div className="hf-hl">
              <span className="hf-day">{formatMonthDay(now.toISOString(), timeZone)}</span>
              <span className="hf-date">{formatWeekday(now.toISOString(), timeZone)}</span>
            </div>
            <div className="hf-hr">
              <span className="hf-clock">
                {clock.time}
                <span className="hf-ampm">{clock.period}</span>
              </span>
            </div>
          </div>
          {data ? (
            <>
              {/* The top of the column, above everything: a hazard is the one
                  thing here that changes what you do on the way out the door.
                  It renders nothing when there is nothing active, which is
                  most days - and on a hazard day it pushes the rest of the
                  column down, which is the right trade. */}
              <AlertBanner alerts={data.alerts} timeZone={data.timezone} />
              {/* Keyed on resetToken so an idle reset remounts the card and
                  lands its selection back on Outside - the same reset that
                  used to collapse the <details> rows. */}
              <div className="hf-zones" key={resetToken}>
                <ClimateCard
                  outside={data.outside}
                  leadZones={zonesPartition?.leads ?? []}
                  timeZone={data.timezone}
                  nowOnServerClock={nowOnServerClock ?? data.generatedAt}
                />
              </div>
              {/* Then the agenda, in full. It used to be the second face of
                  a stage, one horizontal swipe behind the photos - a wall you
                  walk past should not have to be asked what is on today. It is
                  the same CalendarSection page two used to carry, moved rather
                  than copied, so there is exactly one agenda on the wall. The
                  renders-nothing rule survives the move: no events anywhere in
                  the window, no block. */}
              {calendarHasContent && (
                <div className="hf-agenda">
                  <CalendarSection calendar={data.calendar} timeZone={data.timezone} now={now} />
                </div>
              )}
              {/* Last in the column, and the only thing here that is looked at
                  rather than read. Its own data path (usePhotoCarousel), and it
                  returns null until the deck has a photo that loads - a house
                  with no Immich album ends its page at the agenda rather than
                  showing an empty frame. */}
              <PhotoCarousel photos={photos} source={photoSource} timeZone={data.timezone} />
            </>
          ) : error ? (
            <div className="hf-load-error" role="alert">
              Couldn’t load dashboard data — {error}
            </div>
          ) : (
            <DashboardSkeleton />
          )}
          {/* The one hint that page two exists: a quiet pill in the bottom
              padding. Tapping it is the swipe. It sits in the bottom half of
              the screen, which is fine - the top-half rule exists for
              keyboard overlap, and nothing on page one raises a keyboard. */}
          {hasPageTwo && (
            <button type="button" className="hf-page-hint" aria-label="Show controls" onClick={scrollToPageTwo} />
          )}
        </div>
        {hasPageTwo && (
          <div className="hf-p2" ref={pageTwoRef}>
            {/* Tap-density descends: things you trigger, then things you
                watch, then things you adjust, then lists, and the rooms not
                pinned to page one last. Pure reading - the agenda - used to
                close this page and now opens the wall instead. */}
            {data !== null && data.routines.length > 0 && (
              <section className="hf-sec">
                <SectionHead label="Routines" />
                <RoutinesSection routines={data.routines} resetToken={resetToken} />
              </section>
            )}
            {data !== null && data.cameras.length > 0 && (
              <section className="hf-sec">
                <SectionHead label="Cameras" />
                <CamerasSection cameras={data.cameras} onOpen={setManualCameraId} />
              </section>
            )}
            {data !== null && data.panels.length > 0 && (
              <section className="hf-sec">
                <SectionHead label="Panels" />
                <PanelsSection panels={data.panels} onOpen={setPanelId} />
              </section>
            )}
            {/* Outside the snapshot's null-check on purpose: Gather has its
                own data path, so a dashboard API that is down doesn't take
                the shopping list off the wall with it. */}
            {gatherLists.length > 0 && (
              <section className="hf-sec">
                <SectionHead label="Lists" />
                <GatherTile lists={gatherLists} onOpen={setGatherListId} />
              </section>
            )}
            {/* Keyed on resetToken for the <details> collapse - `open` is
                uncontrolled DOM state that no re-render would otherwise undo. */}
            {data !== null && (zonesPartition?.rest.length ?? 0) > 0 && (
              <section className="hf-sec" key={resetToken}>
                <SectionHead label="More rooms" />
                <div className="hf-zones">
                  {(zonesPartition?.rest ?? []).map((zone) => (
                    <ZoneCard
                      key={zone.id}
                      zone={zone}
                      timeZone={data.timezone}
                      nowOnServerClock={nowOnServerClock ?? data.generatedAt}
                    />
                  ))}
                </div>
              </section>
            )}
          </div>
        )}
      </div>
      {/* Inside .hf-page rather than portalled: the circadian palette is inline
          custom properties on that element, and an overlay mounted anywhere
          else would resolve none of them. */}
      {gatherOpen && <GatherOverlay listId={gatherListId} lists={gatherLists} onClose={closeGather} />}
      {healthOpen && (
        <HealthModal entries={healthEntries} timeZone={timeZone} onClose={() => setHealthOpen(false)} />
      )}
      {/* Inside .hf-page for the same reason, and keyed on the panel id so
          switching panels remounts the state hook rather than showing one
          panel's controls under another's name until the first fetch lands. */}
      {openPanel !== undefined && <PanelOverlay key={openPanel.id} panel={openPanel} onClose={() => setPanelId(null)} />}
      {/* Above Gather rather than instead of it: the motion path opens this on
          its own, with nobody's hand on the tablet, so it has to be able to
          interrupt. Keyed on the device id so switching cameras tears the video
          pipeline down and builds a new one, rather than feeding one camera's
          fragments into a SourceBuffer opened for another's codec.
          The name and configured-ness come from the snapshot when it has them;
          both are optional, and a motion event can arrive while the dashboard
          poll is failing. */}
      {cameraDeviceId !== null && (
        <CameraFeedModal
          key={cameraDeviceId}
          deviceId={cameraDeviceId}
          name={shownCamera?.name}
          isConfigured={shownCamera?.isConfigured}
          onClose={closeCamera}
        />
      )}
    </div>
  );
}
