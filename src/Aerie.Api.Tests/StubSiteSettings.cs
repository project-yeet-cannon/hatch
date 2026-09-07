using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Tests;

/// <summary>
/// A SiteSettings snapshot whose parameters are all defaulted, so a test names
/// only the handful of settings it is actually about. Shared across features
/// rather than per-folder: the snapshot is one record with many members, and a
/// copy per test area means every new setting is edited in several places.
/// </summary>
internal sealed class StubSiteSettings(
    string? googleClientId = "client-id.apps.googleusercontent.com",
    string? googleClientSecret = "GOCSPX-secret",
    string? googleOAuthRedirectUri = null,
    string timeZone = "America/New_York",
    int calendarAgendaDays = 2,
    double latitude = 40.7128,
    double longitude = -74.0060,
    string? weatherAlertContact = null,
    int airQualityAlertThresholdAqi = 101,
    int hazardMaxSeverityAgeHours = 48,
    string weatherAlertProvider = HazardProviders.Nws,
    string airQualityProvider = HazardProviders.OpenMeteo,
    string? anthropicApiKey = null,
    string? immichBaseUrl = null,
    string? immichApiKey = null,
    string? claudeSubscriptionToken = null) : ISiteSettingsService
{
    public Task<SiteSettingsSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(new SiteSettingsSnapshot(
        TimeZone: timeZone,
        Latitude: latitude,
        Longitude: longitude,
        WeatherEntity: null,
        ComfortToleranceF: 2m,
        DefaultComfortLowF: 68m,
        DefaultComfortHighF: 72m,
        MediaLibraryBaseUrl: null,
        OverrideBackoffMinutes: 120,
        GoogleClientId: googleClientId,
        GoogleClientSecret: googleClientSecret,
        GoogleOAuthRedirectUri: googleOAuthRedirectUri,
        CalendarAgendaDays: calendarAgendaDays,
        WeatherAlertProvider: weatherAlertProvider,
        AirQualityProvider: airQualityProvider,
        WeatherAlertContact: weatherAlertContact,
        AirQualityAlertThresholdAqi: airQualityAlertThresholdAqi,
        HazardMaxSeverityAgeHours: hazardMaxSeverityAgeHours,
        AnthropicApiKey: anthropicApiKey,
        ImmichBaseUrl: immichBaseUrl,
        ImmichApiKey: immichApiKey,
        ClaudeSubscriptionToken: claudeSubscriptionToken));

    /// <summary>Counted rather than ignored: SettingsController is supposed to call it on every write.</summary>
    public int Invalidations { get; private set; }

    public void Invalidate() => Invalidations++;
}
