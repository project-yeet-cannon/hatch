package family.landis.aeriekiosk

import android.app.admin.DeviceAdminReceiver

/**
 * Marker target for `adb shell dpm set-device-owner`. No behavior of its own —
 * MainActivity checks DevicePolicyManager.isDeviceOwnerApp() and drives lock
 * task setup once this receiver's component is registered as device owner.
 */
class KioskDeviceAdminReceiver : DeviceAdminReceiver()
