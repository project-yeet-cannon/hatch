package family.landis.aeriekiosk

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * A silent PackageInstaller update replaces the running process without
 * relaunching it — Android only guarantees the ACTION_MY_PACKAGE_REPLACED
 * broadcast on the next process start, same as BootCompletedReceiver handles
 * for reboots.
 */
class PackageReplacedReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return

        KioskLogger.init(context)
        KioskLogger.info(
            "Kiosk app updated, relaunching",
            mapOf("versionCode" to BuildConfig.VERSION_CODE, "versionName" to BuildConfig.VERSION_NAME),
        )

        val launchIntent = Intent(context, MainActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(launchIntent)
    }
}
