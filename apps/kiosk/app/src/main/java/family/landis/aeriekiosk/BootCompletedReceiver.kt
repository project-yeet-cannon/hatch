package family.landis.aeriekiosk

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * Relaunches the kiosk after a reboot/power loss so the tablet comes back up
 * straight into lock task mode with no lock screen or manual relaunch needed.
 */
class BootCompletedReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return

        val launchIntent = Intent(context, MainActivity::class.java).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(launchIntent)
    }
}
