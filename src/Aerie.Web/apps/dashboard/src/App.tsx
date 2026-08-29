import { useCallback, useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import type { DashboardData } from './types';
import { getDashboardDataSource } from './dataSource';
import { DEFAULT_TIME_ZONE } from './config';
import { formatClockParts, formatMonthDay, formatWeekday } from './lib/format';
import { useCircadianTheme } from './hooks/useCircadianTheme';
import { ZoneCard } from './components/ZoneCard';
import { OutsideCard } from './components/OutsideCard';
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

const REFRESH_INTERVAL_MS = 60_000;

export function App() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());
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
    let cancelled = false;
    let firstLoad = true;

    const load = () => {
      source
        .getDashboardData()
        .then((snapshot) => {
          if (cancelled) return;
          clientLogger.info(firstLoad ? 'Initial dashboard data loaded' : 'Dashboard data refreshed', {
            zoneCount: snapshot.zones.length,
          });
          firstLoad = false;
          setData(snapshot);
          setError(null);
          notePollSuccess();
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
  const theme = useCircadianTheme(now, data?.sunEvents);
  const clock = formatClockParts(now, timeZone);
  // The snapshot's row for whichever camera is on screen, if it has one. A
  // motion event can arrive while the dashboard poll is failing, so this is
  // allowed to be missing and the modal falls back to fetching the name.
  const shownCamera = cameraDeviceId === null ? undefined : data?.cameras.find((camera) => camera.id === cameraDeviceId);

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
                thing here that changes what you do on the way out the door. It
                renders nothing when there is nothing active, which is most
                days - so sitting here costs a calm day no space at all. */}
            <AlertBanner alerts={data.alerts} timeZone={data.timezone} />
            {/* Keyed on resetToken so an idle reset remounts the cards, which is
                what puts each <details> back to collapsed - `open` is
                uncontrolled DOM state that no re-render would otherwise undo.
                Outside is the one card that stays expanded; every zone below it
                opens only when someone taps it. */}
            <div className="hf-zones" key={resetToken}>
              <OutsideCard outside={data.outside} timeZone={data.timezone} />
              {data.zones.map((zone) => (
                <ZoneCard key={zone.id} zone={zone} timeZone={data.timezone} />
              ))}
            </div>
            {/* Directly under the room cards: the top of the column is the
                house as it is right now, and this is the first thing below it
                that is there to be looked at rather than read. Renders nothing
                until an album is included on the admin Photos page, which is
                what keeps a house that never set Immich up from having a hole
                in its dashboard. */}
            {photos.length > 0 && (
              <PhotoCarousel photos={photos} source={photoSource} timeZone={data.timezone} />
            )}
            {/* Below the zones, above the routines: the agenda is read, the
                routines are touched, so the reachable half of the screen stays
                the tappable one. An agenda whose every day is empty renders
                nothing at all - an empty day is only worth saying when some
                other day isn't. */}
            {data.calendar.some((day) => day.events.length > 0) && (
              <CalendarSection calendar={data.calendar} timeZone={data.timezone} now={now} />
            )}
            {data.routines.length > 0 && <RoutinesSection routines={data.routines} resetToken={resetToken} />}
            {/* Its own row directly below: same tile, same band of the screen,
                but tapping one opens a live feed rather than changing something
                in the house, and that is worth a row break. */}
            {data.cameras.length > 0 && <CamerasSection cameras={data.cameras} onOpen={setManualCameraId} />}
            {/* Below the cameras and above Gather: a panel is the tier between
                a routine's one tap and a sub-UI of its own, and it sits in that
                order on the screen too. */}
            {data.panels.length > 0 && <PanelsSection panels={data.panels} onOpen={setPanelId} />}
          </>
        ) : error ? (
          <div className="hf-note" role="alert" style={{ margin: 0 }}>
            Couldn’t load dashboard data — {error}
          </div>
        ) : (
          <DashboardSkeleton />
        )}
        {/* Outside the snapshot's ternary on purpose: Gather has its own data
            path, so a dashboard API that is down doesn't have to take the
            shopping list off the wall with it. */}
        <GatherTile lists={gatherLists} onOpen={setGatherListId} />
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
