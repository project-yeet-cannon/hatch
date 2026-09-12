namespace Hatch.Api.Models.Kiosk;

/// <summary>Everything the admin app's Provisioning page needs to build the Android QR provisioning payload - see KioskProvisioningController.</summary>
public record ProvisioningInfoDto(
    string SignatureChecksum,
    string ApkDownloadUrl,
    string DeviceAdminComponentName,
    string WifiSsid,
    string WifiPassword,
    string WifiSecurityType,
    string TimeZone);
