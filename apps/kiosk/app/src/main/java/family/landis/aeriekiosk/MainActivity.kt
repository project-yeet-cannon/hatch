package family.landis.aeriekiosk

import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import org.mozilla.geckoview.AllowOrDeny
import org.mozilla.geckoview.GeckoResult
import org.mozilla.geckoview.GeckoRuntime
import org.mozilla.geckoview.GeckoSession
import org.mozilla.geckoview.GeckoView
import org.mozilla.geckoview.WebRequestError
import java.net.HttpURLConnection
import java.net.URL

private const val DASHBOARD_URL = "https://kiosk.landis.family/"
private const val DASHBOARD_HOST = "kiosk.landis.family"

// Derived from DASHBOARD_URL rather than written out again: the host already
// appears here and in KioskLogger, and a third literal is a third thing to
// miss when this app is built for a different installation.
private const val SUN_EVENTS_URL = DASHBOARD_URL + "api/sun-events"

/**
 * What the shell asks after a load "succeeds", because Gecko's idea of success
 * is not the one this app needs. onLoadError fires for DNS, TLS and connection
 * failures; it does *not* fire for a 502 from the ingress or a 500 from the API,
 * which are ordinary responses with an error body - so onPageStop(success=true)
 * would run, the backoff would reset, and the error screen would be *hidden*
 * over whatever the proxy served. The shell believed it was connected.
 *
 * A small JSON endpoint on the same origin, rather than a bridge into the page:
 * a WebExtension port is a large mechanism for one bit of information, and this
 * answers the same question with an HttpURLConnection and no coupling to the
 * bundle - which is the half most likely to be the thing that is broken.
 */
private const val HEALTH_URL = DASHBOARD_URL + "api/app-version/dashboard"
private const val HEALTH_TIMEOUT_MS = 8_000

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
    private lateinit var geckoView: GeckoView
    private lateinit var errorView: KioskErrorView
    private val retryHandler = Handler(Looper.getMainLooper())
    private val reloadHandler = Handler(Looper.getMainLooper())
    private var currentRetryDelayMs = RETRY_DELAY_MS_INITIAL
    private val updateManager = UpdateManager(this)
    private val displayController by lazy { DisplayController(this, SUN_EVENTS_URL) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        KioskLogger.init(this)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        hideSystemBars()

        val content = buildContentView()
        setContentView(content)
        observeImePresence(content)
        loadDashboard()

        enterLockTaskIfDeviceOwner()
        updateManager.start()
        displayController.start()
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

    /**
     * The shell's view of presence, and the reason DisplayController needs no
     * bridge into the page: every touch passes through here on its way to
     * GeckoView, whatever the document does with it afterwards. Returns
     * `super` unconditionally - this observes, it never consumes.
     */
    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        displayController.noteActivity()
        return super.dispatchTouchEvent(event)
    }

    /**
     * The other half of presence: a raised soft keyboard means someone is
     * standing here, even though [dispatchTouchEvent] sees nothing while they
     * type. IME touches go to the IME's own window, so without this a run of
     * Gather entries would dim the panel mid-sentence - the same bug the page's
     * `kioskLifecycle.hold()` exists to prevent, arriving by a different route.
     *
     * API 30+ only, because `Type.ime()` visibility is only dependable from
     * there; below it the value is inferred from system-window insets, which
     * this app's immersive mode already suppresses. Nothing breaks on an older
     * tablet - it simply falls back to touch alone, which is where every kiosk
     * was before this.
     *
     * Returns the insets through `ViewCompat.onApplyWindowInsets` rather than
     * as-is: a listener that returns them directly ends the dispatch, and
     * GeckoView is a child of this view.
     */
    private fun observeImePresence(root: View) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return

        ViewCompat.setOnApplyWindowInsetsListener(root) { view, insets ->
            displayController.setPresenceHold(insets.isVisible(WindowInsetsCompat.Type.ime()))
            ViewCompat.onApplyWindowInsets(view, insets)
        }
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
            // Registered for GeckoView bug 1758212, where content never renders
            // without a delegate - but no longer empty. onCrash/onKill were the
            // defaults, which are no-ops, and a dead content process therefore
            // left the view blank *forever*: onLoadError does not fire,
            // onPageStop does not fire, scheduleRetry is never called, and the
            // error screen never appears. It was the one failure here with no
            // recovery path short of a power cycle.
            setContentDelegate(KioskContentDelegate())
            open(geckoRuntime)
        }

        geckoView = GeckoView(this).apply {
            setSession(geckoSession)
        }
        root.addView(geckoView, FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT)

        errorView = KioskErrorView(this).apply {
            onReload = {
                KioskLogger.info("Error screen: reload tapped")
                // The only reload in the system that can truly bypass the cache,
                // which is why the button belongs here rather than on the page:
                // location.reload() from a broken bundle can be served the same
                // stale document that broke it.
                retryHandler.removeCallbacksAndMessages(null)
                currentRetryDelayMs = RETRY_DELAY_MS_INITIAL
                loadDashboard()
            }
        }
        root.addView(errorView, FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.MATCH_PARENT)

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
            scheduleRetry(LoadFailures.fromErrorCategory(error.category), detail = "code ${'$'}{error.code}")
            // null means "use Gecko's own error page", which on GeckoView is
            // blank. That is fine now and was not before: errorView is on top of
            // the GeckoView and covers whatever it does or doesn't draw.
            return GeckoResult.fromValue(null)
        }
    }

    private inner class KioskProgressDelegate : GeckoSession.ProgressDelegate {
        override fun onPageStop(session: GeckoSession, success: Boolean) {
            // Deliberately does *not* clear the error screen on its own any
            // more - see HEALTH_URL. Gecko reports an error page from the proxy
            // as a successful load, so "the page stopped loading" is not
            // evidence that the dashboard is up. probeOriginHealth decides.
            if (!success) return
            probeOriginHealth()
        }
    }

    /**
     * The content process, separately from the page in it. A crash here is
     * invisible to every other delegate, so this is the only thing that can
     * notice a blank tablet.
     */
    private inner class KioskContentDelegate : GeckoSession.ContentDelegate {
        override fun onCrash(session: GeckoSession) = recover("crash")
        override fun onKill(session: GeckoSession) = recover("kill")

        private fun recover(kind: String) {
            KioskLogger.error("GeckoView content process ended", mapOf("kind" to kind))
            // The session is dead: reopening it is what gets a content process
            // back at all, and re-attaching it is what puts pixels in the view.
            runCatching {
                if (geckoSession.isOpen) geckoSession.close()
                runtime?.let { geckoSession.open(it) }
                geckoView.setSession(geckoSession)
            }.onFailure { KioskLogger.error("Could not rebuild GeckoSession", mapOf("error" to it.message)) }

            // Through the ladder rather than reloading immediately: a crash loop
            // has to back off like anything else, or a page that reliably kills
            // the content process becomes a reboot loop with a wall attached.
            scheduleRetry(KioskFailure.ContentCrashed, detail = kind)
        }
    }

    /**
     * One request against a small same-origin endpoint, off the main thread. A
     * 2xx is what actually clears the error screen and resets the backoff; a
     * 4xx/5xx or a throw means the document that just "loaded" is an error page,
     * and the shell says so instead of hiding the evidence.
     */
    private fun probeOriginHealth() {
        Thread {
            val outcome = runCatching {
                val connection = URL(HEALTH_URL).openConnection() as HttpURLConnection
                connection.connectTimeout = HEALTH_TIMEOUT_MS
                connection.readTimeout = HEALTH_TIMEOUT_MS
                connection.requestMethod = "GET"
                try {
                    connection.responseCode
                } finally {
                    connection.disconnect()
                }
            }

            runOnUiThread {
                val status = outcome.getOrNull()
                when {
                    status != null && status in 200..399 -> {
                        currentRetryDelayMs = RETRY_DELAY_MS_INITIAL
                        errorView.hide()
                    }
                    status != null -> scheduleRetry(LoadFailures.fromHttpStatus(status), detail = "HTTP ${'$'}status")
                    else -> scheduleRetry(KioskFailure.Network, detail = outcome.exceptionOrNull()?.javaClass?.simpleName)
                }
            }
        }.start()
    }

    private fun scheduleRetry(failure: KioskFailure, detail: String? = null) {
        errorView.show(failure, detail)
        // Logged on every rung, so a tablet that is stuck retrying is visible in
        // OpenSearch rather than only to whoever walks past it.
        KioskLogger.warn(
            "Dashboard load failed; retrying",
            mapOf("kind" to LoadFailures.logKind(failure), "detail" to detail, "delayMs" to currentRetryDelayMs),
        )
        retryHandler.postDelayed({ loadDashboard() }, currentRetryDelayMs)
        currentRetryDelayMs = LoadFailures.nextRetryDelayMs(currentRetryDelayMs)
    }

    override fun onDestroy() {
        KioskLogger.info("Kiosk app stopping")
        retryHandler.removeCallbacksAndMessages(null)
        reloadHandler.removeCallbacksAndMessages(null)
        errorView.release()
        updateManager.stop()
        displayController.stop()
        super.onDestroy()
    }
}
