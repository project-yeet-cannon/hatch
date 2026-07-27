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

class MainActivity : AppCompatActivity() {

    companion object {
        // GeckoRuntime may only be created once per process, so it's held here
        // rather than on the activity, which can be recreated.
        private var runtime: GeckoRuntime? = null
    }

    private lateinit var geckoSession: GeckoSession
    private lateinit var reconnectingOverlay: TextView
    private val retryHandler = Handler(Looper.getMainLooper())
    private var currentRetryDelayMs = RETRY_DELAY_MS_INITIAL
    private val updateManager = UpdateManager(this)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        setContentView(buildContentView())
        geckoSession.loadUri(DASHBOARD_URL)

        enterLockTaskIfDeviceOwner()
        updateManager.start()

        KioskLogger.info(
            "Kiosk app started",
            mapOf("versionCode" to BuildConfig.VERSION_CODE, "versionName" to BuildConfig.VERSION_NAME),
        )
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
        retryHandler.postDelayed({ geckoSession.loadUri(DASHBOARD_URL) }, currentRetryDelayMs)
        currentRetryDelayMs = (currentRetryDelayMs * 2).coerceAtMost(RETRY_DELAY_MS_MAX)
    }

    override fun onDestroy() {
        KioskLogger.info("Kiosk app stopping")
        retryHandler.removeCallbacksAndMessages(null)
        updateManager.stop()
        super.onDestroy()
    }
}
