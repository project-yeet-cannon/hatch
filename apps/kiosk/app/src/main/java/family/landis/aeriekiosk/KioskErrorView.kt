package family.landis.aeriekiosk

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * The tablet's floor: what it shows when it cannot show the dashboard.
 *
 * Everything on this screen is drawn from the device alone - no network, no
 * page, no bundle - because the case it exists for is "none of that arrived".
 * That is also why it leads with the time. The time of day is the one thing the
 * wall can always be right about, and a clock with a sentence under it is a
 * tablet still doing part of its job; the "Reconnecting…" TextView this
 * replaces was a black rectangle with one word on it, which is barely
 * distinguishable from broken.
 *
 * Colors are stated rather than themed: circadianTheme.ts is in a bundle that,
 * by hypothesis, is not running, and CircadianBrightness.kt only moves the
 * backlight. These are a hand-picked stand-in for the deep-night palette, which
 * is the right guess for a screen most likely to be seen at 3am.
 */
class KioskErrorView(context: Context) : FrameLayout(context) {

    /** 15s, not 1s: this shows hours and minutes, and a wall tablet should not wake to repaint an unchanged minute. */
    private companion object {
        const val CLOCK_TICK_MS = 15_000L
        const val FALLBACK_ZONE = "America/New_York"

        const val COLOR_BACKGROUND = 0xFF121620.toInt()
        const val COLOR_INK = 0xFFD5D8E0.toInt()
        const val COLOR_MUTED = 0xFF8B90A0.toInt()
        const val COLOR_FAINT = 0xFF4D5464.toInt()
        const val COLOR_BUTTON = 0xFF1B1F2B.toInt()
    }

    private val clockText = TextView(context)
    private val dateText = TextView(context)
    private val messageText = TextView(context)
    private val retryText = TextView(context)
    private val reloadButton = Button(context)
    private val footerText = TextView(context)

    private val ticker = Handler(Looper.getMainLooper())
    private val tick = object : Runnable {
        override fun run() {
            paintClock()
            ticker.postDelayed(this, CLOCK_TICK_MS)
        }
    }

    /**
     * The device's own zone. It is the only source that survives every failure
     * this screen is for - the site timezone lives in a dashboard snapshot that
     * by hypothesis never arrived. Eastern when the device won't say, matching
     * the dashboard's own DEFAULT_TIME_ZONE.
     */
    private val zone: ZoneId = runCatching { ZoneId.systemDefault() }.getOrNull() ?: ZoneId.of(FALLBACK_ZONE)
    private val timeFormat = DateTimeFormatter.ofPattern("h:mm a", Locale.US)
    private val dateFormat = DateTimeFormatter.ofPattern("EEEE, MMMM d", Locale.US)

    var onReload: (() -> Unit)? = null

    init {
        setBackgroundColor(COLOR_BACKGROUND)
        visibility = View.GONE
        // Consumes touches rather than letting them fall through to the
        // GeckoView underneath, which is showing whatever failed.
        isClickable = true

        val column = LinearLayout(context).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(48, 48, 48, 48)
        }

        clockText.apply {
            setTextColor(COLOR_INK)
            textSize = 56f
            gravity = Gravity.CENTER
        }
        dateText.apply {
            setTextColor(COLOR_MUTED)
            textSize = 20f
            gravity = Gravity.CENTER
        }
        messageText.apply {
            setTextColor(COLOR_INK)
            textSize = 17f
            gravity = Gravity.CENTER
            setPadding(0, 56, 0, 0)
        }
        retryText.apply {
            setText(R.string.error_retrying)
            setTextColor(COLOR_MUTED)
            textSize = 14f
            gravity = Gravity.CENTER
            setPadding(0, 10, 0, 0)
        }
        reloadButton.apply {
            setText(R.string.error_reload)
            setTextColor(COLOR_INK)
            setBackgroundColor(COLOR_BUTTON)
            textSize = 16f
            setPadding(64, 28, 64, 28)
            setOnClickListener { onReload?.invoke() }
        }
        footerText.apply {
            setTextColor(COLOR_FAINT)
            textSize = 11f
            gravity = Gravity.CENTER
            setPadding(0, 40, 0, 0)
        }

        column.addView(clockText)
        column.addView(dateText)
        column.addView(messageText)
        column.addView(retryText)
        column.addView(
            reloadButton,
            LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT)
                .apply { topMargin = 32; gravity = Gravity.CENTER_HORIZONTAL },
        )
        column.addView(footerText)

        addView(column, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT))
    }

    /**
     * Put this screen up for the given failure. Safe to call again while it is
     * already showing - a retry that fails the same way should update the
     * sentence, not flicker the screen.
     */
    @SuppressLint("SetTextI18n")
    fun show(failure: KioskFailure, detail: String?) {
        messageText.setText(LoadFailures.messageResFor(failure))
        // Enough to diagnose from a photograph of the screen, which is the only
        // debugging channel a wall tablet has.
        footerText.text = buildString {
            append("Aerie Kiosk ")
            append(BuildConfig.VERSION_NAME)
            append(" (")
            append(BuildConfig.VERSION_CODE)
            append(")")
            if (!detail.isNullOrBlank()) {
                append(" · ")
                append(detail)
            }
        }
        paintClock()
        if (visibility != View.VISIBLE) {
            visibility = View.VISIBLE
            ticker.removeCallbacks(tick)
            ticker.postDelayed(tick, CLOCK_TICK_MS)
        }
    }

    fun hide() {
        if (visibility == View.GONE) return
        visibility = View.GONE
        ticker.removeCallbacks(tick)
    }

    /** Stops the ticker for good. Called from the activity's onDestroy. */
    fun release() = ticker.removeCallbacksAndMessages(null)

    private fun paintClock() {
        val nowLocal = ZonedDateTime.ofInstant(Instant.now(), zone)
        clockText.text = timeFormat.format(nowLocal)
        dateText.text = dateFormat.format(nowLocal)
    }
}
