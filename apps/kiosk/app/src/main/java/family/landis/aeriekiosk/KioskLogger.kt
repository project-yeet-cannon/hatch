package family.landis.aeriekiosk

import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.time.Instant
import java.util.UUID

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
 */
object KioskLogger {
    private val sessionId = UUID.randomUUID().toString()

    fun info(message: String, metadata: Map<String, Any?> = emptyMap()) = send("info", message, metadata)
    fun warn(message: String, metadata: Map<String, Any?> = emptyMap()) = send("warn", message, metadata)
    fun error(message: String, metadata: Map<String, Any?> = emptyMap()) = send("error", message, metadata)

    private fun send(level: String, message: String, metadata: Map<String, Any?>) {
        Thread {
            try {
                val entry = JSONObject().apply {
                    put("app", APP_NAME)
                    put("level", level)
                    put("message", message)
                    put("timestamp", Instant.now().toString())
                    put("sessionId", sessionId)
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
        }.start()
    }
}
