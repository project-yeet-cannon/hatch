using System.Globalization;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>Typed snapshot of the SiteSetting table, with the same defaults DashboardOptions used to carry.</summary>
public record SiteSettingsSnapshot(
    string TimeZone,
    double Latitude,
    double Longitude,
    string? WeatherEntity,
    decimal ComfortToleranceF,
    decimal DefaultComfortLowF,
    decimal DefaultComfortHighF,
    string? MediaLibraryBaseUrl,
    int OverrideBackoffMinutes,
    string? GoogleClientId,
    string? GoogleClientSecret,
    string? GoogleOAuthRedirectUri,
    int CalendarAgendaDays,
    string WeatherAlertProvider,
    string AirQualityProvider,
    string? WeatherAlertContact,
    int AirQualityAlertThresholdAqi,
    int HazardMaxSeverityAgeHours,
    string? AnthropicApiKey,
    string? ImmichBaseUrl,
    string? ImmichApiKey);

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
                Latitude: ParseDouble(values, SiteSettingKeys.Latitude, 40.7128),
                Longitude: ParseDouble(values, SiteSettingKeys.Longitude, -74.0060),
                WeatherEntity: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.WeatherEntity)),
                ComfortToleranceF: ParseDecimal(values, SiteSettingKeys.ComfortToleranceF, 2m),
                DefaultComfortLowF: ParseDecimal(values, SiteSettingKeys.DefaultComfortLowF, 68m),
                DefaultComfortHighF: ParseDecimal(values, SiteSettingKeys.DefaultComfortHighF, 72m),
                MediaLibraryBaseUrl: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.MediaLibraryBaseUrl)),
                // Two hours: long enough that someone who reached for the
                // thermostat gets the evening they wanted, short enough that a
                // one-off adjustment doesn't silently disable the controller
                // for the rest of the week.
                OverrideBackoffMinutes: ParseInt(values, SiteSettingKeys.OverrideBackoffMinutes, 120),
                GoogleClientId: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.GoogleClientId)),
                // The read side has to hand back something usable, so the
                // client secret is deobfuscated here - the same trade
                // KioskProvisioningController makes with the Wi-Fi password.
                GoogleClientSecret: Deobfuscated(values.GetValueOrDefault(SiteSettingKeys.GoogleClientSecret)),
                GoogleOAuthRedirectUri: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.GoogleOAuthRedirectUri)),
                // Today plus tomorrow: the window the kiosk agenda is sized for.
                CalendarAgendaDays: ParseInt(values, SiteSettingKeys.CalendarAgendaDays, 2),
                // Both providers are keyless, so they can default to on: an
                // operator who never opens the settings page still gets alerts.
                // HazardProviderResolver decides what an unknown name means -
                // the snapshot only reports what was typed.
                WeatherAlertProvider: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.WeatherAlertProvider)) ?? HazardProviders.Nws,
                AirQualityProvider: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.AirQualityProvider)) ?? HazardProviders.OpenMeteo,
                WeatherAlertContact: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.WeatherAlertContact)),
                // 101 is the bottom of "Unhealthy for Sensitive Groups". Below
                // it the air is fine and the banner would be furniture.
                AirQualityAlertThresholdAqi: ParseInt(values, SiteSettingKeys.AirQualityAlertThresholdAqi, 101),
                // Two days, matching the agenda half of the kiosk: a warning
                // that starts Thursday is not what a Tuesday glance is for.
                HazardMaxSeverityAgeHours: ParseInt(values, SiteSettingKeys.HazardMaxSeverityAgeHours, 48),
                // Deobfuscated for the same reason as the Google secret above:
                // the game module has to hand it to an SDK, not display it.
                AnthropicApiKey: Deobfuscated(values.GetValueOrDefault(SiteSettingKeys.AnthropicApiKey)),
                // Trailing slashes trimmed here rather than at every call site:
                // the operator types a host into a form, and "https://photos/"
                // and "https://photos" are the same server.
                ImmichBaseUrl: NullIfEmpty(values.GetValueOrDefault(SiteSettingKeys.ImmichBaseUrl))?.TrimEnd('/'),
                // Deobfuscated for the same reason as the two above: the Photos
                // module hands it to Immich as a header, not to a screen.
                ImmichApiKey: Deobfuscated(values.GetValueOrDefault(SiteSettingKeys.ImmichApiKey)));

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

    /// <summary>
    /// Reverses SettingsController's protection. Unlike that controller's own
    /// call sites, this one is on the path of every settings read in the app,
    /// so a value that isn't readable - hand-edited in the DB, or written by a
    /// newer Aerie under a scheme this one doesn't know - degrades to "unset"
    /// rather than throwing out of the snapshot. SecretProtector.Unprotect
    /// already answers null for both, which is why this no longer catches.
    /// </summary>
    private static string? Deobfuscated(string? value) => SecretProtector.Unprotect(value);

    private static decimal ParseDecimal(IReadOnlyDictionary<string, string> values, string key, decimal fallback) =>
        values.TryGetValue(key, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static double ParseDouble(IReadOnlyDictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
