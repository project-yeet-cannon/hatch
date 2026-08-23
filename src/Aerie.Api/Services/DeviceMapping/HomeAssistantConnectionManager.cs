using Aerie.Api.Common;
using Aerie.Api.Ef;
using HADotNet.Core;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// The Home Assistant connection as configured in SiteSettings, with the token
/// already deobfuscated. ToString is overridden because the compiler-generated
/// record one prints every member - a bearer token that reaches a log line is a
/// bearer token in OpenSearch, and this type is handed to a BackgroundService
/// whose whole job is logging what it's doing.
/// </summary>
public record HomeAssistantConnection(string Host, string Port, string Token)
{
    /// <summary>REST base address for HADotNet's ClientFactory.</summary>
    public string BaseAddress => $"http://{Host}:{Port}/";

    /// <summary>The WebSocket API endpoint (see HomeAssistantEventListener). Plain ws:// to match BaseAddress's plain http:// - both assume HA is reached over the LAN rather than through a TLS terminator.</summary>
    public Uri WebSocketUri => new($"ws://{Host}:{Port}/api/websocket");

    public override string ToString() => $"{Host}:{Port}";
}

public interface IHomeAssistantConnectionManager
{
    /// <summary>The current HomeAssistantHost/Port/Token SiteSettings, or null if any of the three isn't set yet.</summary>
    Task<HomeAssistantConnection?> ResolveAsync(CancellationToken ct);

    /// <summary>(Re)initializes the HADotNet ClientFactory from the current HomeAssistantHost/Port/Token SiteSettings. No-ops if any of the three aren't set yet.</summary>
    Task ApplyAsync(CancellationToken ct);
}

public class HomeAssistantConnectionManager(IDbContextFactory<AerieContext> dbFactory) : IHomeAssistantConnectionManager
{
    public async Task<HomeAssistantConnection?> ResolveAsync(CancellationToken ct)
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
            return null;

        // TryReveal rather than Deobfuscate: a token hand-edited into something
        // that isn't base64 reads as "not configured yet", which the callers
        // already handle, instead of throwing out of a background loop.
        if (SecretObfuscator.TryReveal(obfuscatedToken) is not { } token)
            return null;

        return new HomeAssistantConnection(host, port, token);
    }

    public async Task ApplyAsync(CancellationToken ct)
    {
        if (await ResolveAsync(ct) is not { } connection) return;

        ClientFactory.Initialize(connection.BaseAddress, connection.Token);
    }
}
