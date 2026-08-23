package family.landis.aeriekiosk

import android.app.Activity
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import android.view.WindowManager
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.time.OffsetDateTime
import java.time.ZoneId
import kotlin.math.abs

private const val TAG = "DisplayController"

private const val PREFS_NAME = "aerie-display"
private const val KEY_DAWN = "sun.dawn"
private const val KEY_SUNRISE = "sun.sunrise"
private const val KEY_SUNSET = "sun.sunset"
private const val KEY_DUSK = "sun.dusk"

/**
 * How often the backlight is recomputed. The evening transition spans two
 * hours, so a 60s tick divides it into ~120 steps of well under 1% each -
 * below the threshold where a step reads as a flicker, which is why this
 * doesn't animate between ticks.
 */
private const val TICK_MS = 60_000L

/** Sun events move by a minute or two a day; re-reading them 4x daily is free and self-healing. */
private const val SUN_REFRESH_MS = 6 * 60 * 60 * 1000L

/** Sooner than the steady refresh, because until the first fetch lands there may be no curve at all. */
private const val SUN_RETRY_MS = 15 * 60 * 1000L

/**
 * How long after the last touch the backlight drops to its idle floor.
 *
 * **Mirrored from `IDLE_DIM_AFTER_MS` in
 * src/Aerie.Web/apps/dashboard/src/lib/kioskIdleTimings.ts**, which holds the
 * whole ladder - the page's idle reset above this rung - and explains why the
 * two numbers are only correct relative to each other. The shell cannot import that file, so this is the second copy, on the
 * same terms as CircadianBrightness.kt's transition constants: change one and
 * change the other, or the panel and the page start disagreeing about whether
 * anyone is standing here.
 *
 * Generous on purpose. Touch is the only presence signal this app has until
 * docs/plans/kiosk_brightness.md Phase 4, and reading a wall display is a thing
 * you do with your hands in your pockets.
 */
private const val IDLE_DIM_AFTER_MS = 120_000L

/** A drag fires per frame; there is no reason to re-arm a two-minute timer at 60Hz. */
private const val ACTIVITY_THROTTLE_MS = 250L

/**
 * How long the dim takes, and how often it steps.
 *
 * **The dim ramps and the restore snaps**, and the asymmetry is the whole
 * design: a slow fade *down* is a room getting quieter, which nobody is
 * watching; a slow fade *up* is a screen taking four seconds to answer a
 * finger, which reads as a tablet that has crashed. 10 steps a second is
 * plenty - brightness is not motion, and [BRIGHTNESS_EPSILON] discards the
 * frames too small to see anyway, so a shallow ramp costs proportionally fewer
 * window relayouts than a deep one.
 */
private const val RAMP_DURATION_MS = 4_000L
private const val RAMP_FRAME_MS = 100L

/**
 * Phase 1 defaults, hardcoded here and destined for a per-kiosk profile on the
 * server (docs/plans/kiosk_brightness.md, Phase 3). A hallway and a kitchen
 * want different night floors and this cannot express that yet - what it can
 * do is stop every tablet being a full-brightness lamp at 3am, which is the
 * larger half of the problem and needs none of that machinery.
 */
private val DEFAULT_CURVE = BrightnessCurve(day = 1.0f, amber = 0.35f, night = 0.05f)

/**
 * Where the backlight sits once nobody has touched the tablet for
 * [IDLE_DIM_AFTER_MS]. The same three keyframes as [DEFAULT_CURVE], sampled on
 * the same phase function, so the idle floor travels the day rather than being
 * one number that is too dark at noon and too bright at midnight.
 *
 * **Night is a true 0**, which on this window means
 * `BRIGHTNESS_OVERRIDE_OFF` - documented as the panel's *lowest* backlight, not
 * a powered-off display. The screen stays on and stays touch-responsive; only
 * the lamp goes away. docs/plans/kiosk_brightness.md pairs that with a
 * full-black view to hide whatever the panel still leaks at its minimum, and
 * that half is deliberately not built yet: a few nights of watching what 0
 * actually looks like in an unlit hallway answers whether it is needed at all,
 * and answers the plan's open question about where this hardware clamps.
 */
private val DEFAULT_IDLE_CURVE = BrightnessCurve(day = 0.45f, amber = 0.15f, night = 0.0f)

/** Below this, re-applying window attributes is churn nobody can see. */
private const val BRIGHTNESS_EPSILON = 0.004f

/**
 * How far sun events may be rolled forward before they're discarded instead.
 * Sunset moves by a couple of minutes a day, so a week's roll is worth maybe a
 * quarter hour of error at the transition edges - invisible on a backlight
 * curve, and far better than the alternative. Past that the error compounds
 * into something that would dim the wall at the wrong time of day, and no
 * curve beats a wrong one.
 */
private const val MAX_ROLL_FORWARD_DAYS = 7L

/**
 * Moves the tablet's backlight along the same curve the dashboard's palette
 * already travels ([circadianPhase]), so a near-black page at 3am is actually
 * dark rather than merely dark-coloured, and drops it further still once
 * nobody has touched the tablet for a while.
 *
 * Brightness is set on the activity's own window rather than through
 * `Settings.System.SCREEN_BRIGHTNESS` (which Device Owner could write via
 * `DevicePolicyManager.setSystemSetting`). Same visible effect, no permission,
 * and - the reason it's the right choice rather than merely the easy one - the
 * value dies with the window. A crash or a bad deploy cannot strand a tablet at
 * an unreadable global brightness that someone has to fix by hand at the wall.
 *
 * `FLAG_KEEP_SCREEN_ON` stays set in MainActivity: the display should never
 * time out, only dim.
 *
 * ## Presence
 *
 * Two signals, both fed in from MainActivity, because this class deliberately
 * owns no views:
 *
 * - [noteActivity] from `dispatchTouchEvent`, which sees every touch on the way
 *   to GeckoView and so needs no bridge into the page.
 * - [setPresenceHold] while the soft keyboard is up. Touches on an IME go to
 *   the IME's own window, not this activity's, so a long run of typing looks
 *   exactly like an empty room to the signal above - and dimming the panel in
 *   the middle of someone entering a shopping list is the same bug the page's
 *   `hold()` exists to prevent (lib/kioskLifecycle.ts).
 */
class DisplayController(
    private val activity: Activity,
    private val sunEventsUrl: String,
    private val curve: BrightnessCurve = DEFAULT_CURVE,
    private val idleCurve: BrightnessCurve = DEFAULT_IDLE_CURVE,
) {
    private val handler = Handler(Looper.getMainLooper())
    private val prefs = activity.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private var events: SunEvents? = null
    private var appliedBrightness = Float.NaN
    private var fetchInFlight = false

    private var idle = false
    private var presenceHeld = false

    // elapsedRealtime, not wall clock: an NTP correction must not read as two
    // minutes of nobody being here. UpdateManager times its poll the same way.
    private var lastActivityAt = 0L

    private var rampActive = false
    private var rampFrom = 0f
    private var rampTarget = 0f
    private var rampStartedAt = 0L

    private val tick = object : Runnable {
        override fun run() {
            apply()
            handler.postDelayed(this, TICK_MS)
        }
    }

    fun start() {
        events = readCachedEvents()
        if (events == null) {
            // First boot, or first boot since this feature existed. The screen
            // stays at the system default until a fetch lands rather than
            // guessing a curve from the clock alone - a wrongly-dimmed wall
            // display is worse than an undimmed one, and this resolves itself
            // within a poll.
            KioskLogger.info("Display: no cached sun events; backlight unmanaged until first fetch")
        }
        // Armed before anyone has touched anything, which is the point: a
        // tablet that boots at 3am and is never touched should arrive at the
        // idle floor on its own rather than wait for a first finger to start
        // the clock.
        lastActivityAt = SystemClock.elapsedRealtime()
        armIdle()
        handler.post(tick)
        handler.post(fetchSunEvents)
    }

    fun stop() {
        handler.removeCallbacksAndMessages(null)
        // The handler drops the ramp's queued frame; this drops the flag that
        // says one is still in flight, so nothing later mistakes a torn-down
        // controller for a ramping one.
        rampActive = false
    }

    /**
     * Someone touched the screen. Called from `MainActivity.dispatchTouchEvent`
     * on every touch, so it is deliberately cheap in the common case.
     */
    fun noteActivity() {
        // Ahead of the throttle: leaving the idle floor is the one thing here a
        // finger is waiting on, and it has to happen on the first event of a
        // gesture rather than the first one past a quarter-second window.
        if (idle) wake()

        val now = SystemClock.elapsedRealtime()
        if (now - lastActivityAt < ACTIVITY_THROTTLE_MS) return
        lastActivityAt = now
        // While held there is nothing to re-arm; releasing the hold is what
        // starts the clock again.
        if (!presenceHeld) armIdle()
    }

    /**
     * Hold the display lit regardless of touch, for as long as something else
     * says someone is there. Idempotent; the release counts as a fresh touch,
     * so the dim gets a full timeout rather than whatever was left of the one
     * the hold cancelled.
     */
    fun setPresenceHold(held: Boolean) {
        if (presenceHeld == held) return
        presenceHeld = held

        if (held) {
            handler.removeCallbacks(idleTimeout)
            if (idle) wake()
            KioskLogger.info("Display: presence hold on; idle dim suspended")
        } else {
            lastActivityAt = SystemClock.elapsedRealtime()
            armIdle()
            KioskLogger.info("Display: presence hold off")
        }
    }

    private fun armIdle() {
        handler.removeCallbacks(idleTimeout)
        handler.postDelayed(idleTimeout, IDLE_DIM_AFTER_MS)
    }

    private val idleTimeout = Runnable {
        if (idle) return@Runnable
        idle = true
        KioskLogger.info("Display: idle, dimming", mapOf("idleAfterMs" to IDLE_DIM_AFTER_MS))
        apply(ramp = true)
    }

    private fun wake() {
        idle = false
        cancelRamp()
        apply()
        KioskLogger.info("Display: touched, backlight restored", mapOf("brightness" to appliedBrightness))
    }

    /**
     * Recomputes the target from the current phase and idle state and moves the
     * panel to it - snapping by default, ramping when [ramp] asks for it.
     *
     * The 60s [tick] never ramps: its steps are sub-1% by construction and a
     * snap is invisible. It does, however, let an in-flight ramp finish rather
     * than cutting across it, which is the only reason [ramp] is not simply a
     * property of who called.
     */
    private fun apply(ramp: Boolean = false) {
        val now = System.currentTimeMillis()
        // Aligned on every tick rather than only at load: a fetch that fails
        // for two days leaves in-memory events just as stale as cached ones,
        // and the failure mode is the same wrongly-dimmed daytime wall.
        val current = events?.let { alignToDay(it, now, ZoneId.systemDefault(), MAX_ROLL_FORWARD_DAYS) }
        if (current == null) {
            // Events too old to roll. Handing the override back is the point of
            // detecting staleness at all - simply returning here would freeze
            // the last value applied, which after a long outage is exactly the
            // 5% night floor stuck through the following afternoon.
            cancelRamp()
            releaseOverride()
            return
        }
        val phase = circadianPhase(now, current)
        val target = brightnessFor(phase, if (idle) idleCurve else curve).coerceIn(0f, 1f)

        val first = appliedBrightness.isNaN()
        if (!first && (ramp || rampActive)) {
            startRamp(target)
            return
        }

        if (!setBrightness(target)) return

        // Only the arrival at a phase is worth a log line; the ~120 steps
        // inside a transition are not, and would bury everything else in the
        // shared aerie-logs index. Idle and wake log at their own call sites.
        if (first || phase is CircadianPhase.Day || phase is CircadianPhase.Night) {
            KioskLogger.info(
                "Display: backlight set",
                mapOf("phase" to phase.javaClass.simpleName, "brightness" to target, "idle" to idle),
            )
        }
    }

    /**
     * Writes the window attribute, unless the change is too small to see.
     * Returns whether it actually wrote, so a caller can tie a log line to a
     * visible change rather than to a tick.
     */
    private fun setBrightness(value: Float, force: Boolean = false): Boolean {
        if (value == appliedBrightness) return false
        if (!force && !appliedBrightness.isNaN() && abs(value - appliedBrightness) < BRIGHTNESS_EPSILON) return false

        val layoutParams = activity.window.attributes
        layoutParams.screenBrightness = value
        activity.window.attributes = layoutParams
        appliedBrightness = value
        Log.d(TAG, "brightness=$value idle=$idle")
        return true
    }

    private fun startRamp(target: Float) {
        // From wherever the panel actually is, not from wherever the last ramp
        // started: a retarget mid-ramp (the 60s tick landing inside one) has to
        // continue from here or it would jump backwards to pick up the new line.
        rampFrom = appliedBrightness
        rampTarget = target
        rampStartedAt = SystemClock.elapsedRealtime()
        if (rampActive) return
        rampActive = true
        handler.post(rampFrame)
    }

    private fun cancelRamp() {
        rampActive = false
        handler.removeCallbacks(rampFrame)
    }

    private val rampFrame = object : Runnable {
        override fun run() {
            if (!rampActive) return
            val elapsed = SystemClock.elapsedRealtime() - rampStartedAt
            val progress = (elapsed.toFloat() / RAMP_DURATION_MS).coerceIn(0f, 1f)
            if (progress >= 1f) {
                // Forced, because the last frames of a shallow ramp are each
                // below the epsilon and would otherwise leave the panel parked a
                // hair short of the floor it was aiming for.
                setBrightness(rampTarget, force = true)
                rampActive = false
                return
            }
            // Computed from the endpoints each frame rather than accumulated, so
            // the frames the epsilon discards cost precision rather than drift.
            setBrightness(rampFrom + (rampTarget - rampFrom) * progress)
            handler.postDelayed(this, RAMP_FRAME_MS)
        }
    }

    /**
     * Returns the window to the device's own preferred brightness. Idempotent,
     * so the stale path can call it every tick without churning the window.
     */
    private fun releaseOverride() {
        if (appliedBrightness.isNaN()) return

        val layoutParams = activity.window.attributes
        layoutParams.screenBrightness = WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE
        activity.window.attributes = layoutParams
        appliedBrightness = Float.NaN
        KioskLogger.warn("Display: sun events too stale to use; backlight returned to system default")
    }

    private val fetchSunEvents = object : Runnable {
        override fun run() {
            if (fetchInFlight) return
            fetchInFlight = true
            Thread {
                val fetched = try {
                    requestSunEvents()
                } catch (e: Exception) {
                    Log.w(TAG, "Sun events fetch failed", e)
                    KioskLogger.warn("Display: sun events fetch failed", mapOf("error" to e.message))
                    null
                }

                handler.post {
                    fetchInFlight = false
                    if (fetched != null) {
                        events = fetched
                        cacheEvents(fetched)
                        apply()
                    }
                    // A failed fetch keeps whatever curve is already loaded -
                    // yesterday's events are wrong by a minute or two, which is
                    // nothing next to reverting to no curve at all.
                    handler.postDelayed(this, if (fetched != null) SUN_REFRESH_MS else SUN_RETRY_MS)
                }
            }.start()
        }
    }

    private fun requestSunEvents(): SunEvents? {
        val connection = URL(sunEventsUrl).openConnection() as HttpURLConnection
        connection.connectTimeout = 10_000
        connection.readTimeout = 10_000
        return try {
            if (connection.responseCode != HttpURLConnection.HTTP_OK) {
                KioskLogger.warn("Display: sun events unreadable", mapOf("httpStatus" to connection.responseCode))
                return null
            }
            val body = connection.inputStream.bufferedReader().use { it.readText() }
            val json = JSONObject(body)
            SunEvents(
                dawnMs = epochMillis(json.getString("dawn")),
                sunriseMs = epochMillis(json.getString("sunrise")),
                sunsetMs = epochMillis(json.getString("sunset")),
                duskMs = epochMillis(json.getString("dusk")),
            )
        } finally {
            connection.disconnect()
        }
    }

    /**
     * The server sends `DateTimeOffset`, which serialises with a real offset
     * (`...-05:00`) rather than always `Z`. `Instant.parse` rejects that;
     * `OffsetDateTime` accepts both.
     */
    private fun epochMillis(value: String): Long = OffsetDateTime.parse(value).toInstant().toEpochMilli()

    private fun cacheEvents(value: SunEvents) {
        prefs.edit()
            .putLong(KEY_DAWN, value.dawnMs)
            .putLong(KEY_SUNRISE, value.sunriseMs)
            .putLong(KEY_SUNSET, value.sunsetMs)
            .putLong(KEY_DUSK, value.duskMs)
            .apply()
    }

    /**
     * Survives a reboot, which is the case that matters: a power cut at dusk
     * shouldn't bring the wall back at full brightness and hold it there until
     * the network returns.
     */
    private fun readCachedEvents(): SunEvents? {
        if (!prefs.contains(KEY_DAWN)) return null
        return SunEvents(
            dawnMs = prefs.getLong(KEY_DAWN, 0),
            sunriseMs = prefs.getLong(KEY_SUNRISE, 0),
            sunsetMs = prefs.getLong(KEY_SUNSET, 0),
            duskMs = prefs.getLong(KEY_DUSK, 0),
        )
    }
}
