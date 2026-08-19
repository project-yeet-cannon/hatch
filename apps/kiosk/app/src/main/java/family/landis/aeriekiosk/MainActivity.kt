package family.landis.aeriekiosk

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import org.mozilla.geckoview.AllowOrDeny
import org.mozilla.geckoview.GeckoResult
import org.mozilla.geckoview.GeckoRuntime
import org.mozilla.geckoview.GeckoSession
import org.mozilla.geckoview.GeckoView
import org.mozilla.geckoview.WebRequestError

private const val DASHBOARD_URL = "https://kiosk.landis.family/"
private const val DASHBOARD_HOST = "kiosk.landis.family"
private const val RETRY_DELAY_MS_INITIAL = 2_000L
private const val RETRY_DELAY_MS_MAX = 30_000L

// Backstop only. The dashboard reloads itself when it notices a newer build
// (src/Aerie.Web/apps/dashboard/src/hooks/useKioskLifecycle.ts), and that is the
// mechanism meant to pick deploys up. This exists for the case that one can't
// cover: a bundle broken badly enough that its own lifecycle code never runs,
// on a wall tablet nobody is going to power-cycle. Long interval because it is
// a floor on staleness, not a refresh strategy.
private const val PERIODIC_RELOAD_MS = 12 * 60 * 60 * 1000L

class MainActivity : AppCompatActivity() {

    companion object {
        // GeckoRuntime may only be created once per process, so it's held here
        // rather than on the activity, which can be recreated.
        private var runtime: GeckoRuntime? = null
    }

    private lateinit var geckoSession: GeckoSession
    private lateinit var reconnectingOverlay: TextView
    private val retryHandler = Handler(Looper.getMainLooper())
    private val reloadHandler = Handler(Looper.getMainLooper())
    private var currentRetryDelayMs = RETRY_DELAY_MS_INITIAL
    private val updateManager = UpdateManager(this)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        KioskLogger.init(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        setContentView(buildContentView())
        loadDashboard()

        enterLockTaskIfDeviceOwner()
        updateManager.start()
        reloadHandler.postDelayed(periodicReload, PERIODIC_RELOAD_MS)

        KioskLogger.info(
            "Kiosk app started",
            mapOf("versionCode" to BuildConfig.VERSION_CODE, "versionName" to BuildConfig.VERSION_NAME),
        )
    }

    /**
     * LOAD_FLAGS_BYPASS_CACHE because this runs exactly when a stale document
     * is least acceptable and least likely to be evicted on its own: first boot
     * after a power cycle, and after a failed load. Gecko is otherwise free to
     * apply heuristic freshness to the dashboard's index.html and serve the
     * previous deploy's copy - which names the previous deploy's bundle - so a
     * power cycle, the one recovery action available to someone standing at the
     * tablet, could come back to the same stale screen. The API now sends
     * Cache-Control on those documents (Program.cs) so this is belt and braces
     * going forward, but it is what makes a power cycle a reliable fix on a
     * tablet that cached index.html *before* that header existed.
     *
     * The full bundle is ~1.2 MB; this path runs on boot and on reconnect, not
     * on the periodic refresh, so the re-download is rare by construction.
     */
    private fun loadDashboard() {
        geckoSession.load(
            GeckoSession.Loader()
                .uri(DASHBOARD_URL)
                .flags(GeckoSession.LOAD_FLAGS_BYPASS_CACHE),
        )
    }

    private val periodicReload = object : Runnable {
        override fun run() {
            KioskLogger.info("Periodic backstop reload", mapOf("intervalMs" to PERIODIC_RELOAD_MS))
            geckoSession.reload(GeckoSession.LOAD_FLAGS_BYPASS_CACHE)
            reloadHandler.postDelayed(this, PERIODIC_RELOAD_MS)
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun hideSystemBars() {
        val controller = WindowInsetsControllerCompat(window, window.decorView)
        controller.hide(WindowInsetsCompat.Type.systemBars())
        controller.systemBarsBehavior =
            WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    }

    /**
     * The tablet is provisioned as Device Owner via `adb shell dpm set-device-owner`
     * (see apps/kiosk deployment notes) before this app is ever expected to be the
     * only thing on screen. Lock task features are set to NONE rather than left at
     * their defaults because the default set still permits pulling down the
     * notification shade and reaching the recents/overview screen — both are
     * escape routes out of the kiosk that this app exists to close off.
     */
    private fun enterLockTaskIfDeviceOwner() {
        val dpm = getSystemService(DEVICE_POLICY_SERVICE) as DevicePolicyManager
        val admin = ComponentName(this, KioskDeviceAdminReceiver::class.java)
        if (!dpm.isDeviceOwnerApp(packageName)) return

        dpm.setLockTaskPackages(admin, arrayOf(packageName))
        dpm.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_NONE)
        startLockTask()
    }

    private fun buildContentView(): FrameLayout {
        val root = FrameLayout(this)

        val geckoRuntime = runtime ?: GeckoRuntime.create(this).also { runtime = it }
        geckoSession = GeckoSession().apply {
            navigationDelegate = KioskNavigationDelegate()
            progressDelegate = KioskProgressDelegate()
            // Empty delegate works around GeckoView bug 1758212, where content
            // never renders without one registered, even a no-op.
            setContentDelegate(object : GeckoSession.ContentDelegate {})
            open(geckoRuntime)
        }

        val geckoView = GeckoView(this).apply {
            setSession(geckoSession)
        }
        root.addView(geckoView, FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT)

        reconnectingOverlay = TextView(this).apply {
            text = getString(R.string.reconnecting_message)
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
            setBackgroundColor(Color.BLACK)
            visibility = View.GONE
        }
        root.addView(
            reconnectingOverlay,
            FrameLayout.LayoutParams.MATCH_PARENT,
            FrameLayout.LayoutParams.MATCH_PARENT,
        )

        return root
    }

    // Keeps the session pinned to the dashboard's own origin so there's no way
    // to navigate (via a stray link, redirect, etc.) to a different site.
    private inner class KioskNavigationDelegate : GeckoSession.NavigationDelegate {
        override fun onLoadRequest(
            session: GeckoSession,
            request: GeckoSession.NavigationDelegate.LoadRequest,
        ): GeckoResult<AllowOrDeny> {
            val allowed = Uri.parse(request.uri).host == DASHBOARD_HOST
            return GeckoResult.fromValue(if (allowed) AllowOrDeny.ALLOW else AllowOrDeny.DENY)
        }

        override fun onLoadError(
            session: GeckoSession,
            uri: String?,
            error: WebRequestError,
        ): GeckoResult<String> {
            scheduleRetry()
            return GeckoResult.fromValue(null)
        }
    }

    private inner class KioskProgressDelegate : GeckoSession.ProgressDelegate {
        override fun onPageStop(session: GeckoSession, success: Boolean) {
            if (!success) return
            currentRetryDelayMs = RETRY_DELAY_MS_INITIAL
            reconnectingOverlay.visibility = View.GONE
        }
    }

    private fun scheduleRetry() {
        reconnectingOverlay.visibility = View.VISIBLE
        retryHandler.postDelayed({ loadDashboard() }, currentRetryDelayMs)
        currentRetryDelayMs = (currentRetryDelayMs * 2).coerceAtMost(RETRY_DELAY_MS_MAX)
    }

    override fun onDestroy() {
        KioskLogger.info("Kiosk app stopping")
        retryHandler.removeCallbacksAndMessages(null)
        reloadHandler.removeCallbacksAndMessages(null)
        updateManager.stop()
        super.onDestroy()
    }
}
