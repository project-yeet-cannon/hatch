package family.landis.aeriekiosk

import java.time.Instant
import java.time.ZoneId
import java.time.temporal.ChronoUnit

/**
 * The backlight's half of the circadian cycle, and a deliberate port of
 * `getCircadianPhase` / the keyframe table from
 * src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts and
 * src/Aerie.Web/apps/dashboard/src/theme/tokens.ts.
 *
 * ## Why this is duplicated rather than shared
 *
 * The dashboard fades its *palette* across the day. CSS cannot reach the
 * backlight, so on an LCD that produces a very dark page lit by a very bright
 * lamp. The panel has to travel the same curve as the pixels, and only the
 * native shell can move it.
 *
 * The shell can't ask the page what phase it's in: GeckoView has no
 * `addJavascriptInterface`, so a shell/page bridge means bundling a
 * WebExtension with native messaging. Against that, a table of keyframe times
 * duplicated behind a stable server contract (`SunEvents`) is the cheaper
 * trade - see docs/plans/kiosk_brightness.md.
 *
 * **[CIRCADIAN_TIMELINE] is load-bearing on both sides.** Its rows mirror the
 * `circadianTimeline` table in tokens.ts one for one, in order, by name, and
 * the `brightness`/`idleBrightness` columns *there* are the source those two
 * floats are copied from - the palette author picks them next to the colors
 * they belong with. If a keyframe moves, is added, or is dropped in tokens.ts
 * it has to move here too, or the screen starts dimming at a visibly different
 * moment from the page drawn on it. That divergence is the failure mode this
 * comment exists to prevent; CircadianBrightnessTest mirrors
 * circadianTheme.test.ts case for case so it surfaces as a red test rather than
 * as a wrong-looking wall.
 */

private const val MINUTE_MS = 60_000L

/**
 * The day's four sun events as epoch milliseconds, mirroring the server's
 * `SunEvents` record (src/Aerie.Api/Models/Dashboard/DashboardData.cs).
 *
 * All four are read now. [duskMs] in particular is civil dusk, which is where
 * the palette inverts from dark ink on light cards to the reverse - so it is
 * also where the backlight makes its steepest move of the evening.
 */
data class SunEvents(
    val dawnMs: Long,
    val sunriseMs: Long,
    val sunsetMs: Long,
    val duskMs: Long,
)

/** Which sun event a keyframe hangs off. */
enum class SunEvent { DAWN, SUNRISE, SUNSET, DUSK }

/**
 * One keyframe: when it happens, and where the panel sits when it does.
 *
 * The web side's row carries a palette here as well; this half needs only the
 * two floats, and keeps [name] so a log line and a scrubber readout name the
 * same moment.
 */
data class MoodStop(
    val name: String,
    val event: SunEvent,
    val offsetMinutes: Long,
    val brightness: Float,
    val idleBrightness: Float,
)

/**
 * Phase 1 defaults, hardcoded here and destined for a per-kiosk profile on the
 * server (docs/plans/kiosk_brightness.md, Phase 3). A hallway and a kitchen
 * want different night floors and this cannot express that yet - what it can do
 * is stop every tablet being a full-brightness lamp at 3am, which is the larger
 * half of the problem and needs none of that machinery.
 *
 * **The idle column bottoms out at a true 0**, which on this window means
 * `BRIGHTNESS_OVERRIDE_OFF` - documented as the panel's *lowest* backlight, not
 * a powered-off display. The screen stays on and stays touch-responsive; only
 * the lamp goes away. docs/plans/kiosk_brightness.md pairs that with a
 * full-black view to hide whatever the panel still leaks at its minimum, and
 * that half is deliberately not built yet.
 */
val CIRCADIAN_TIMELINE: List<MoodStop> = listOf(
    MoodStop("deepNight", SunEvent.DAWN, -210, 0.04f, 0.0f),
    MoodStop("lateNight", SunEvent.DAWN, -45, 0.05f, 0.0f),
    MoodStop("firstLight", SunEvent.DAWN, 0, 0.10f, 0.04f),
    MoodStop("dawnGlow", SunEvent.DAWN, 0, 0.12f, 0.05f),
    MoodStop("sunriseGlow", SunEvent.SUNRISE, 0, 0.30f, 0.12f),
    MoodStop("morning", SunEvent.SUNRISE, 80, 0.62f, 0.28f),
    MoodStop("day", SunEvent.SUNRISE, 200, 1.0f, 0.45f),
    MoodStop("dayHold", SunEvent.SUNSET, -165, 1.0f, 0.45f),
    MoodStop("goldenHour", SunEvent.SUNSET, -45, 0.68f, 0.30f),
    MoodStop("sunsetGlow", SunEvent.SUNSET, 0, 0.40f, 0.17f),
    MoodStop("afterglow", SunEvent.DUSK, 0, 0.20f, 0.08f),
    MoodStop("duskFall", SunEvent.DUSK, 0, 0.16f, 0.06f),
    MoodStop("night", SunEvent.DUSK, 75, 0.06f, 0.0f),
    MoodStop("deepNightEnd", SunEvent.DUSK, 225, 0.04f, 0.0f),
)

/** Where `nowMs` falls on the timeline: the keyframe at or before it, and how far past. */
data class CircadianPhase(val index: Int, val progress: Float, val name: String)

/**
 * Absolute times for each keyframe, in epoch ms.
 *
 * Clamped to be non-decreasing rather than sorted, exactly as the web side is:
 * at extreme latitudes the sun events can bunch up or invert, and a *sort*
 * would reorder keyframes. Clamping can only collapse a segment to zero length,
 * which is already the normal case - the two pairs that share `dawn` and `dusk`
 * are where the page's palette inverts.
 */
fun timelineTimes(events: SunEvents, timeline: List<MoodStop> = CIRCADIAN_TIMELINE): LongArray {
    val times = LongArray(timeline.size)
    timeline.forEachIndexed { i, stop ->
        val anchor = when (stop.event) {
            SunEvent.DAWN -> events.dawnMs
            SunEvent.SUNRISE -> events.sunriseMs
            SunEvent.SUNSET -> events.sunsetMs
            SunEvent.DUSK -> events.duskMs
        }
        val at = anchor + stop.offsetMinutes * MINUTE_MS
        times[i] = if (i == 0) at else maxOf(at, times[i - 1])
    }
    return times
}

/** Which keyframes [nowMs] sits between, and how far across. */
fun circadianPhase(
    nowMs: Long,
    events: SunEvents,
    timeline: List<MoodStop> = CIRCADIAN_TIMELINE,
): CircadianPhase {
    val times = timelineTimes(events, timeline)

    // The *last* keyframe at or before now, which is what makes the shared
    // instants resolve to their far side - the same rule as the web side, so
    // the panel and the page agree about which keyframe they are on.
    var index = 0
    for (i in times.indices) {
        if (times[i] <= nowMs) index = i
    }

    val last = timeline.size - 1
    if (index >= last) return CircadianPhase(last, 0f, timeline[last].name)
    return CircadianPhase(index, progressBetween(nowMs, times[index], times[index + 1]), timeline[index].name)
}

/** The backlight level for a phase, 0..1. */
fun brightnessFor(
    phase: CircadianPhase,
    idle: Boolean,
    timeline: List<MoodStop> = CIRCADIAN_TIMELINE,
): Float {
    val from = timeline[phase.index]
    val to = timeline[minOf(phase.index + 1, timeline.size - 1)]
    val level = { stop: MoodStop -> if (idle) stop.idleBrightness else stop.brightness }
    return lerp(level(from), level(to), phase.progress).coerceIn(0f, 1f)
}

/**
 * Rolls a day's sun events forward onto the calendar day containing [nowMs],
 * or returns null if they are too old to be worth rolling.
 *
 * The dashboard never needs this - it is handed fresh events on every
 * `/api/dashboard` poll and a failed poll just leaves the page as it was. The
 * shell does, because it holds a *cached* set across reboots and network
 * outages, and stale events don't degrade gracefully here: yesterday's dusk is
 * more than a day behind `now`, so [circadianPhase] reads past the end of the
 * timeline and returns the night floor. A tablet rebooting at noon on a dead
 * network would dim itself to that floor in full daylight.
 *
 * Rolling by whole days works because the cycle is ~24h periodic; the residual
 * drift is a couple of minutes per day, which a backlight cannot show.
 */
fun alignToDay(events: SunEvents, nowMs: Long, zone: ZoneId, maxRollForwardDays: Long): SunEvents? {
    val eventsDay = Instant.ofEpochMilli(events.sunriseMs).atZone(zone).toLocalDate()
    val today = Instant.ofEpochMilli(nowMs).atZone(zone).toLocalDate()
    val days = ChronoUnit.DAYS.between(eventsDay, today)

    if (days == 0L) return events
    if (days < 0 || days > maxRollForwardDays) return null

    val shift = days * 24 * 60 * 60 * 1000L
    return SunEvents(
        dawnMs = events.dawnMs + shift,
        sunriseMs = events.sunriseMs + shift,
        sunsetMs = events.sunsetMs + shift,
        duskMs = events.duskMs + shift,
    )
}

private fun lerp(a: Float, b: Float, t: Float): Float = a + (b - a) * t

private fun progressBetween(t: Long, start: Long, end: Long): Float {
    if (end <= start) return 1f
    return ((t - start).toFloat() / (end - start).toFloat()).coerceIn(0f, 1f)
}
