package family.landis.aeriekiosk

import android.content.Context
import android.provider.Settings
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

private const val TAG = "KioskLogger"
private const val LOG_ENDPOINT = "https://kiosk.landis.family/api/ui-logs"
private const val APP_NAME = "kiosk-android"

/**
 * Ships native-shell lifecycle/update events to the same `/api/ui-logs`
 * endpoint the dashboard web app's clientLogger.ts posts to (see
 * docs/kiosk-architecture.md) — same-origin from kiosk.<DOMAIN>, so these
 * lines land in the same aerie-logs OpenSearch index and Dashboards view as
 * everything else, distinguished by `app` instead of a separate pipeline.
 *
 * Fire-and-forget on a background thread: losing an occasional log line to a
 * killed process is an acceptable trade for never blocking an activity or
 * receiver lifecycle callback on a network round-trip.
 *
 * One worker thread with a bounded queue rather than a Thread per line. At a
 * healthy tablet's volume the difference is nothing; the moment that matters is
 * the one where volume goes up, which is precisely when something is wrong -
 * a reconnect ladder logging every rung against an endpoint that is not
 * answering, each line holding a thread for its 5s timeout. Bounded and
 * dropping rather than growing, for the same reason: a logger is not allowed to
 * be the thing that takes the wall down.
 */
object KioskLogger {
    private val sessionId = UUID.randomUUID().toString()

    private const val QUEUE_CAPACITY = 64

    private val sender = ThreadPoolExecutor(
        1, 1, 30, TimeUnit.SECONDS,
        ArrayBlockingQueue(QUEUE_CAPACITY),
        { runnable -> Thread(runnable, "KioskLogger").apply { isDaemon = true } },
        // Drop the newest rather than blocking the caller. The caller is an
        // activity or a broadcast receiver on the main thread, and a full queue
        // already means the endpoint is not answering - so blocking would trade
        // a lost log line for a frozen wall.
        ThreadPoolExecutor.DiscardPolicy(),
    ).apply { allowCoreThreadTimeOut(true) }

    // ANDROID_ID rather than a generated-and-stored UUID: it's already
    // unique per device (per app-signing-key) and survives process/app
    // restarts with no storage of our own to manage. init() must run before
    // the first send() - every call site (Activity/BroadcastReceiver) has a
    // Context available, see MainActivity.onCreate and the receivers.
    private var deviceId: String? = null

    fun init(context: Context) {
        if (deviceId != null) return
        deviceId = Settings.Secure.getString(context.applicationContext.contentResolver, Settings.Secure.ANDROID_ID)
    }

    fun info(message: String, metadata: Map<String, Any?> = emptyMap()) = send("info", message, metadata)
    fun warn(message: String, metadata: Map<String, Any?> = emptyMap()) = send("warn", message, metadata)
    fun error(message: String, metadata: Map<String, Any?> = emptyMap()) = send("error", message, metadata)

    private fun send(level: String, message: String, metadata: Map<String, Any?>) {
        // Stamped here rather than on the worker, so a line that waits in the
        // queue still reports when it happened rather than when it was sent.
        val timestamp = Instant.now().toString()
        val task = Runnable {
            try {
                val entry = JSONObject().apply {
                    put("app", APP_NAME)
                    put("level", level)
                    put("message", message)
                    put("timestamp", timestamp)
                    put("sessionId", sessionId)
                    put("deviceId", deviceId)
                    put("metadata", JSONObject(metadata.filterValues { it != null }))
                }
                val body = JSONArray().put(entry).toString().toByteArray(Charsets.UTF_8)

                val connection = URL(LOG_ENDPOINT).openConnection() as HttpURLConnection
                connection.requestMethod = "POST"
                connection.doOutput = true
                connection.connectTimeout = 5_000
                connection.readTimeout = 5_000
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.use { it.write(body) }
                connection.responseCode // force the request to actually execute
                connection.disconnect()
            } catch (e: Exception) {
                Log.w(TAG, "Failed to ship log line: $message", e)
            }
        }

        try {
            sender.execute(task)
        } catch (e: RejectedExecutionException) {
            // DiscardPolicy does not throw, so this is only reachable after a
            // shutdown. logcat still has it either way.
            Log.w(TAG, "Dropped log line: $message", e)
        }
    }
}
