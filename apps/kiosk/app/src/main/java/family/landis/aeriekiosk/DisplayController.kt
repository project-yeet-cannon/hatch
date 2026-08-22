package family.landis.aeriekiosk

import android.app.Activity
import android.content.Context
import android.os.Handler
import android.os.Looper
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
 * Phase 1 defaults, hardcoded here and destined for a per-kiosk profile on the
 * server (docs/plans/kiosk_brightness.md, Phase 3). A hallway and a kitchen
 * want different night floors and this cannot express that yet - what it can
 * do is stop every tablet being a full-brightness lamp at 3am, which is the
 * larger half of the problem and needs none of that machinery.
 *
 * `night` is deliberately low rather than off. Panels clamp `screenBrightness`
 * at some minimum of their own and the value where that happens is unmeasured
 * on this hardware; a value below the clamp is simply the clamp, so erring low
 * costs nothing and erring high leaves a lit hallway.
 */
private val DEFAULT_CURVE = BrightnessCurve(day = 1.0f, amber = 0.35f, night = 0.05f)

/**
 * Never drive the window to a true 0. On some panels 0 means "off", and an off
 * screen does not wake on touch - the touchscreen powers down with it, leaving
 * the power button as the only way back on a wall-mounted tablet. Real
 * screen-off is a Phase 4 feature, gated on something that can wake it.
 */
private const val MIN_BRIGHTNESS = 0.01f

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
 * already travels ([CircadianBrightness]), so a near-black page at 3am is
 * actually dark rather than merely dark-coloured.
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
 */
class DisplayController(
    private val activity: Activity,
    private val sunEventsUrl: String,
    private val curve: BrightnessCurve = DEFAULT_CURVE,
) {
    private val handler = Handler(Looper.getMainLooper())
    private val prefs = activity.applicationContext.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private var events: SunEvents? = null
    private var appliedBrightness = Float.NaN
    private var fetchInFlight = false

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
        handler.post(tick)
        handler.post(fetchSunEvents)
    }

    fun stop() {
        handler.removeCallbacksAndMessages(null)
    }

    private fun apply() {
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
            releaseOverride()
            return
        }
        val phase = circadianPhase(now, current)
        val target = brightnessFor(phase, curve).coerceIn(MIN_BRIGHTNESS, 1.0f)

        if (!appliedBrightness.isNaN() && abs(target - appliedBrightness) < BRIGHTNESS_EPSILON) return

        val layoutParams = activity.window.attributes
        layoutParams.screenBrightness = target
        activity.window.attributes = layoutParams

        // Only the arrival at a phase is worth a log line; the ~120 steps
        // inside a transition are not, and would bury everything else in the
        // shared aerie-logs index.
        if (appliedBrightness.isNaN() || phase is CircadianPhase.Day || phase is CircadianPhase.Night) {
            KioskLogger.info(
                "Display: backlight set",
                mapOf("phase" to phase.javaClass.simpleName, "brightness" to target),
            )
        }
        appliedBrightness = target
        Log.d(TAG, "brightness=$target phase=$phase")
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
