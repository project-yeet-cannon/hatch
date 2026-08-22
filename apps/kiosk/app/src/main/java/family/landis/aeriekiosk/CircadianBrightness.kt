package family.landis.aeriekiosk

import java.time.Instant
import java.time.ZoneId
import java.time.temporal.ChronoUnit

/**
 * The backlight's half of the circadian cycle, and a deliberate port of
 * `getCircadianPhase` from
 * src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts.
 *
 * ## Why this is duplicated rather than shared
 *
 * The dashboard already fades its *palette* from daylight through amber to
 * near-black across a night. CSS cannot reach the backlight, so on an LCD that
 * produces a very dark page lit by a very bright lamp. The panel has to travel
 * the same curve as the pixels, and only the native shell can move it.
 *
 * The shell can't ask the page what phase it's in: GeckoView has no
 * `addJavascriptInterface`, so a shell/page bridge means bundling a
 * WebExtension with native messaging. Against that, ~40 lines of arithmetic
 * duplicated behind a stable server contract (`SunEvents`) is the cheaper
 * trade - see docs/plans/kiosk_brightness.md.
 *
 * **The constants below are load-bearing on both sides.** If the transition
 * lead or duration changes in circadianTheme.ts, it has to change here too, or
 * the screen starts dimming at a visibly different moment from the page drawn
 * on it. That divergence is the failure mode this comment exists to prevent;
 * CircadianBrightnessTest mirrors circadianTheme.test.ts case for case so it
 * surfaces as a red test rather than as a wrong-looking wall.
 */

/** How long before actual sunset the evening transition begins. */
private const val EVENING_TRANSITION_LEAD_MINUTES = 35L

/** How long the evening transition takes, start to full night. */
private const val EVENING_TRANSITION_DURATION_HOURS = 2L

private const val MINUTE_MS = 60_000L
private const val HOUR_MS = 3_600_000L

/**
 * The day's four sun events as epoch milliseconds, mirroring the server's
 * `SunEvents` record (src/Aerie.Api/Models/Dashboard/DashboardData.cs).
 *
 * [duskMs] is carried but unread: the phase function needs only three of the
 * four, and the type stays whole so it maps 1:1 onto what the endpoint returns
 * rather than being a lossy subset a future reader has to reconcile.
 */
data class SunEvents(
    val dawnMs: Long,
    val sunriseMs: Long,
    val sunsetMs: Long,
    val duskMs: Long,
)

sealed interface CircadianPhase {
    data object Day : CircadianPhase
    data object Night : CircadianPhase
    data class EveningTransition(val progress: Float) : CircadianPhase
    data class MorningTransition(val progress: Float) : CircadianPhase
}

/**
 * Backlight levels at the three keyframes, named to match `CircadianTokenSets`
 * in the dashboard's theme so the two are obviously the same curve sampled for
 * different purposes.
 */
data class BrightnessCurve(
    val day: Float,
    val amber: Float,
    val night: Float,
)

/** Which phase of the cycle [nowMs] falls in, and how far through a transition it is. */
fun circadianPhase(nowMs: Long, events: SunEvents): CircadianPhase {
    val eveningStart = events.sunsetMs - EVENING_TRANSITION_LEAD_MINUTES * MINUTE_MS
    val eveningEnd = eveningStart + EVENING_TRANSITION_DURATION_HOURS * HOUR_MS

    return when {
        nowMs < events.dawnMs -> CircadianPhase.Night
        nowMs < events.sunriseMs ->
            CircadianPhase.MorningTransition(progressBetween(nowMs, events.dawnMs, events.sunriseMs))
        nowMs < eveningStart -> CircadianPhase.Day
        nowMs < eveningEnd ->
            CircadianPhase.EveningTransition(progressBetween(nowMs, eveningStart, eveningEnd))
        else -> CircadianPhase.Night
    }
}

/**
 * The backlight level for a phase. Transitions blend *through* the amber
 * keyframe rather than straight from day to night, exactly as the palette does
 * - the point of the amber midpoint is that the fade is not linear, and a
 * linear backlight under a non-linear palette would read as the screen and the
 * page disagreeing about what time it is.
 */
fun brightnessFor(phase: CircadianPhase, curve: BrightnessCurve): Float = when (phase) {
    is CircadianPhase.Day -> curve.day
    is CircadianPhase.Night -> curve.night
    is CircadianPhase.EveningTransition ->
        blendThroughAmber(curve.day, curve.amber, curve.night, phase.progress)
    is CircadianPhase.MorningTransition ->
        blendThroughAmber(curve.night, curve.amber, curve.day, phase.progress)
}

/**
 * Rolls a day's sun events forward onto the calendar day containing [nowMs],
 * or returns null if they are too old to be worth rolling.
 *
 * The dashboard never needs this - it is handed fresh events on every
 * `/api/dashboard` poll and a failed poll just leaves the page as it was. The
 * shell does, because it holds a *cached* set across reboots and network
 * outages, and stale events don't degrade gracefully here: yesterday's sunset
 * is more than a day behind `now`, so [circadianPhase] reads the far side of
 * the evening transition and returns Night. A tablet rebooting at noon on a
 * dead network would dim itself to a night floor in full daylight.
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

/** First half of a transition blends start->amber, second half blends amber->end. */
private fun blendThroughAmber(start: Float, amber: Float, end: Float, progress: Float): Float =
    if (progress <= 0.5f) lerp(start, amber, progress / 0.5f)
    else lerp(amber, end, (progress - 0.5f) / 0.5f)

private fun lerp(a: Float, b: Float, t: Float): Float = a + (b - a) * t

private fun progressBetween(t: Long, start: Long, end: Long): Float {
    if (end <= start) return 1f
    return ((t - start).toFloat() / (end - start).toFloat()).coerceIn(0f, 1f)
}
