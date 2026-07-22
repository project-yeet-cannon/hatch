using Aerie.Api.Common;
using Aerie.Api.Ef;
using HADotNet.Core;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.DeviceMapping;

public interface IHomeAssistantConnectionManager
{
    /// <summary>(Re)initializes the HADotNet ClientFactory from the current HomeAssistantHost/Port/Token SiteSettings. No-ops if any of the three aren't set yet.</summary>
    Task ApplyAsync(CancellationToken ct);
}

public class HomeAssistantConnectionManager(IDbContextFactory<AerieContext> dbFactory) : IHomeAssistantConnectionManager
{
    public async Task ApplyAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var values = await db.SiteSettings.AsNoTracking()
            .Where(s => s.Key == SiteSettingKeys.HomeAssistantHost
                || s.Key == SiteSettingKeys.HomeAssistantPort
                || s.Key == SiteSettingKeys.HomeAssistantToken)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var host = values.GetValueOrDefault(SiteSettingKeys.HomeAssistantHost);
        var port = values.GetValueOrDefault(SiteSettingKeys.HomeAssistantPort);
        var obfuscatedToken = values.GetValueOrDefault(SiteSettingKeys.HomeAssistantToken);
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(obfuscatedToken))
            return;

        ClientFactory.Initialize($"http://{host}:{port}/", SecretObfuscator.Deobfuscate(obfuscatedToken));
    }
}
