using System.Globalization;
using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>Typed snapshot of the SiteSetting table, with the same defaults DashboardOptions used to carry.</summary>
public record SiteSettingsSnapshot(
    string TimeZone,
    string SunEntity,
    string? WeatherEntity,
    decimal ComfortToleranceF,
    decimal DefaultComfortLowF,
    decimal DefaultComfortHighF);

public interface ISiteSettingsService
{
    Task<SiteSettingsSnapshot> GetAsync(CancellationToken ct);
}

/// <summary>
/// Replaces IOptions&lt;DashboardOptions&gt; as the read side of the dashboard's
/// scalar settings (docs/device-architecture.md Phase 5) - SettingsController
/// is still the write side, CRUDing EfSiteSetting directly. Cached with a short
/// TTL rather than invalidated on write, since the controller has no signal
/// back to this singleton; a stale read is bounded to CacheTtl instead of
/// living forever like a bare in-memory cache would.
/// </summary>
public class SiteSettingsService(IDbContextFactory<AerieContext> dbFactory, TimeProvider time) : ISiteSettingsService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim gate = new(1, 1);
    private SiteSettingsSnapshot? cached;
    private DateTimeOffset expiresAt;

    public async Task<SiteSettingsSnapshot> GetAsync(CancellationToken ct)
    {
        if (cached is { } fresh && time.GetUtcNow() < expiresAt) return fresh;

        await gate.WaitAsync(ct);
        try
        {
            if (cached is { } stillFresh && time.GetUtcNow() < expiresAt) return stillFresh;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var values = await db.SiteSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);

            var snapshot = new SiteSettingsSnapshot(
                TimeZone: values.GetValueOrDefault(SiteSettingKeys.TimeZone, "America/New_York"),
                SunEntity: values.GetValueOrDefault(SiteSettingKeys.SunEntity, "sun.sun"),
                WeatherEntity: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.WeatherEntity)),
                ComfortToleranceF: ParseDecimal(values, SiteSettingKeys.ComfortToleranceF, 2m),
                DefaultComfortLowF: ParseDecimal(values, SiteSettingKeys.DefaultComfortLowF, 68m),
                DefaultComfortHighF: ParseDecimal(values, SiteSettingKeys.DefaultComfortHighF, 72m));

            cached = snapshot;
            expiresAt = time.GetUtcNow() + CacheTtl;
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static decimal ParseDecimal(IReadOnlyDictionary<string, string> values, string key, decimal fallback) =>
        values.TryGetValue(key, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
