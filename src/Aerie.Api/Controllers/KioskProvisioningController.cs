using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.Kiosk;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Controllers;

/// <summary>
/// Backs the admin app's Provisioning page (docs/ Phase 3 kiosk provisioning):
/// hands back everything needed to build the Android QR provisioning payload
/// (signing-cert checksum, APK URL, Wi-Fi credentials, time zone).
///
/// Deliberately on AuthGate's allow-list (docs/auth-architecture.md): a tablet
/// at first boot holds no grant, and nothing here is more sensitive than an APK
/// URL and the Wi-Fi credentials the tablet is about to join with anyway. It's
/// reached same-origin like UiLogsController, so no CORS handling is needed.
/// </summary>
[ApiController]
[Route("api/kiosk")]
public class KioskProvisioningController(
    AerieContext db,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : ControllerBase
{
    private const string DeviceAdminComponentName = "family.landis.aeriekiosk/.KioskDeviceAdminReceiver";

    [HttpGet("provisioning-info")]
    public async Task<ProvisioningInfoDto> GetProvisioningInfo(CancellationToken ct)
    {
        var settings = await db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SiteSettingKeys.KioskWifiSsid
                || s.Key == SiteSettingKeys.KioskWifiPassword
                || s.Key == SiteSettingKeys.KioskWifiSecurityType
                || s.Key == SiteSettingKeys.TimeZone)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var storedWifiPassword = settings.GetValueOrDefault(SiteSettingKeys.KioskWifiPassword, "");

        var filesClient = httpClientFactory.CreateClient("KioskFiles");
        var checksum = (await filesClient.GetStringAsync("signature-checksum.txt", ct)).Trim();

        return new ProvisioningInfoDto(
            SignatureChecksum: checksum,
            ApkDownloadUrl: $"https://files.{configuration["DOMAIN"]}/app-release.apk",
            DeviceAdminComponentName: DeviceAdminComponentName,
            WifiSsid: settings.GetValueOrDefault(SiteSettingKeys.KioskWifiSsid, ""),
            WifiPassword: SecretProtector.Unprotect(storedWifiPassword) ?? "",
            WifiSecurityType: settings.GetValueOrDefault(SiteSettingKeys.KioskWifiSecurityType, ""),
            TimeZone: settings.GetValueOrDefault(SiteSettingKeys.TimeZone, ""));
    }
}
