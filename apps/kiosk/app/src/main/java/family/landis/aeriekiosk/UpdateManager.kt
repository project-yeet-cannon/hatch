package family.landis.aeriekiosk

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Log
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

private const val TAG = "UpdateManager"
private const val VERSION_URL = "https://files.landis.family/version.json"
private const val APK_URL = "https://files.landis.family/app-release.apk"

// Deploys often land right after a dev session, and iteration on this app
// tends to happen in focused spurts — so check aggressively for the first
// hour after launch, then settle down to a steady background poll. Windows
// are measured from UpdateManager.start(), not wall-clock time of day.
private const val FAST_WINDOW_MS = 20 * 60 * 1000L
private const val FAST_INTERVAL_MS = 60 * 1000L
private const val MEDIUM_WINDOW_MS = 60 * 60 * 1000L
private const val MEDIUM_INTERVAL_MS = 5 * 60 * 1000L
private const val STEADY_INTERVAL_MS = 6 * 60 * 60 * 1000L

/**
 * Polls files.<DOMAIN> — the same static host CI publishes the APK to for QR
 * provisioning (see docs/kiosk-architecture.md) — for a newer versionCode,
 * and if found, downloads and silently installs it via PackageInstaller.
 * Device Owner apps may commit installs with no user-facing confirmation
 * (see https://developer.android.com/work/dpc/build-dpc#silent_install),
 * which is what makes unattended self-update possible at all: these tablets
 * have no Play Store and nobody on-site to tap "install".
 *
 * The APK itself isn't checksummed here — Android's installer already
 * rejects a commit whose signing certificate doesn't match the installed
 * app's, which is the property that actually matters for an update.
 */
class UpdateManager(private val context: Context) {
    private val handler = Handler(Looper.getMainLooper())
    private var checkInFlight = false
    private var startedAtElapsedMs = 0L

    private val checkRunnable = object : Runnable {
        override fun run() {
            checkForUpdate()
            handler.postDelayed(this, nextIntervalMs())
        }
    }

    fun start() {
        startedAtElapsedMs = SystemClock.elapsedRealtime()
        handler.postDelayed(checkRunnable, FAST_INTERVAL_MS)
    }

    fun stop() {
        handler.removeCallbacks(checkRunnable)
    }

    // SystemClock.elapsedRealtime() rather than wall-clock time, so this
    // can't be thrown off by a timezone/NTP correction mid-window.
    private fun nextIntervalMs(): Long {
        val sinceStart = SystemClock.elapsedRealtime() - startedAtElapsedMs
        return when {
            sinceStart < FAST_WINDOW_MS -> FAST_INTERVAL_MS
            sinceStart < MEDIUM_WINDOW_MS -> MEDIUM_INTERVAL_MS
            else -> STEADY_INTERVAL_MS
        }
    }

    private fun checkForUpdate() {
        if (checkInFlight) return
        checkInFlight = true
        Thread {
            try {
                val latestVersionCode = fetchLatestVersionCode()
                if (latestVersionCode == null) {
                    KioskLogger.warn("Update check: version.json unreadable")
                } else if (latestVersionCode > BuildConfig.VERSION_CODE) {
                    Log.i(TAG, "Update available: $latestVersionCode > ${BuildConfig.VERSION_CODE}")
                    KioskLogger.info(
                        "Update check: newer version available",
                        mapOf("currentVersionCode" to BuildConfig.VERSION_CODE, "latestVersionCode" to latestVersionCode),
                    )
                    downloadAndInstall(latestVersionCode)
                } else {
                    KioskLogger.info("Update check: already up to date", mapOf("versionCode" to BuildConfig.VERSION_CODE))
                }
            } catch (e: Exception) {
                Log.w(TAG, "Update check failed", e)
                KioskLogger.warn("Update check failed", mapOf("error" to e.message))
            } finally {
                checkInFlight = false
            }
        }.start()
    }

    private fun fetchLatestVersionCode(): Int? {
        val connection = URL(VERSION_URL).openConnection() as HttpURLConnection
        connection.connectTimeout = 10_000
        connection.readTimeout = 10_000
        return try {
            if (connection.responseCode != HttpURLConnection.HTTP_OK) return null
            val body = connection.inputStream.bufferedReader().use { it.readText() }
            JSONObject(body).getInt("versionCode")
        } finally {
            connection.disconnect()
        }
    }

    private fun downloadAndInstall(targetVersionCode: Int) {
        KioskLogger.info("Downloading update", mapOf("targetVersionCode" to targetVersionCode))

        val connection = URL(APK_URL).openConnection() as HttpURLConnection
        connection.connectTimeout = 15_000
        connection.readTimeout = 30_000
        try {
            if (connection.responseCode != HttpURLConnection.HTTP_OK) {
                Log.w(TAG, "APK download failed: HTTP ${connection.responseCode}")
                KioskLogger.warn(
                    "Update download failed",
                    mapOf("targetVersionCode" to targetVersionCode, "httpStatus" to connection.responseCode),
                )
                return
            }

            val installer = context.packageManager.packageInstaller
            val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
            val sessionId = installer.createSession(params)

            installer.openSession(sessionId).use { session ->
                session.openWrite("update", 0, -1).use { out ->
                    connection.inputStream.buffered().copyTo(out)
                    session.fsync(out)
                }

                val pendingIntent = PendingIntent.getBroadcast(
                    context,
                    sessionId,
                    Intent(context, InstallResultReceiver::class.java).putExtra(
                        InstallResultReceiver.EXTRA_TARGET_VERSION_CODE,
                        targetVersionCode,
                    ),
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
                )
                KioskLogger.info("Installing update", mapOf("targetVersionCode" to targetVersionCode))
                session.commit(pendingIntent.intentSender)
            }
        } finally {
            connection.disconnect()
        }
    }
}
