package family.landis.aeriekiosk

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.util.Log

private const val TAG = "InstallResultReceiver"

/**
 * Target of the PendingIntent PackageInstaller.Session.commit() reports back
 * to. As Device Owner the commit itself is already silent (no confirmation
 * UI); this only logs the outcome for adb logcat debugging. A successful
 * install triggers Android's normal ACTION_MY_PACKAGE_REPLACED broadcast,
 * handled separately by PackageReplacedReceiver.
 */
class InstallResultReceiver : BroadcastReceiver() {

    companion object {
        const val EXTRA_TARGET_VERSION_CODE = "family.landis.aeriekiosk.extra.TARGET_VERSION_CODE"
    }

    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        val targetVersionCode = intent.getIntExtra(EXTRA_TARGET_VERSION_CODE, -1)

        if (status == PackageInstaller.STATUS_SUCCESS) {
            Log.i(TAG, "Update installed successfully")
            KioskLogger.info("Update installed successfully", mapOf("targetVersionCode" to targetVersionCode))
        } else {
            Log.w(TAG, "Update install failed: status=$status message=$message")
            KioskLogger.warn(
                "Update install failed",
                mapOf("targetVersionCode" to targetVersionCode, "status" to status, "message" to message),
            )
        }
    }
}
