package family.landis.aeriekiosk

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.graphics.Color
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.FrameLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

private const val DASHBOARD_URL = "https://kiosk.landis.family/"
private const val DASHBOARD_HOST = "kiosk.landis.family"
private const val RETRY_DELAY_MS_INITIAL = 2_000L
private const val RETRY_DELAY_MS_MAX = 30_000L

class MainActivity : AppCompatActivity() {

    private lateinit var webView: WebView
    private lateinit var reconnectingOverlay: TextView
    private val retryHandler = Handler(Looper.getMainLooper())
    private var currentRetryDelayMs = RETRY_DELAY_MS_INITIAL

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        setContentView(buildContentView())
        webView.loadUrl(DASHBOARD_URL)

        enterLockTaskIfDeviceOwner()
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

        webView = WebView(this).apply {
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            webViewClient = KioskWebViewClient()
        }
        root.addView(webView, FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT)

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

    private inner class KioskWebViewClient : WebViewClient() {

        // Keeps the WebView pinned to the dashboard's own origin so there's no way
        // to navigate (via a stray link, redirect, etc.) to a different site.
        override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
            return request.url.host != DASHBOARD_HOST
        }

        override fun onPageFinished(view: WebView, url: String?) {
            super.onPageFinished(view, url)
            currentRetryDelayMs = RETRY_DELAY_MS_INITIAL
            reconnectingOverlay.visibility = View.GONE
        }

        override fun onReceivedError(
            view: WebView,
            request: WebResourceRequest,
            error: WebResourceError,
        ) {
            super.onReceivedError(view, request, error)
            if (!request.isForMainFrame) return
            scheduleRetry()
        }

        private fun scheduleRetry() {
            reconnectingOverlay.visibility = View.VISIBLE
            retryHandler.postDelayed({ webView.loadUrl(DASHBOARD_URL) }, currentRetryDelayMs)
            currentRetryDelayMs = (currentRetryDelayMs * 2).coerceAtMost(RETRY_DELAY_MS_MAX)
        }
    }

    override fun onDestroy() {
        retryHandler.removeCallbacksAndMessages(null)
        super.onDestroy()
    }
}
