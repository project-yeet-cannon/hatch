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
    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
        if (status == PackageInstaller.STATUS_SUCCESS) {
            Log.i(TAG, "Update installed successfully")
        } else {
            Log.w(TAG, "Update install failed: status=$status message=$message")
        }
    }
}
