using Hatch.Api.Ef;
using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Services.Hazards;

public interface IHazardService
{
    /// <summary>Everything outside worth saying right now, most severe first. Empty on a calm, clean-air day - which is most days, and is why the banner renders nothing at all when this is empty.</summary>
    Task<IReadOnlyList<HazardAlert>> GetAlertsAsync(CancellationToken ct);
}

/// <summary>
/// The dashboard's read path for outdoor hazards: a query over what
/// SyncOutdoorHazards cached, per the manifest's rule that nothing in a request
/// path calls a provider.
///
/// Two shaping decisions live here rather than in the job. Rows are filtered to
/// the *configured* provider, so switching providers (or turning one off)
/// clears the wall on the next poll instead of leaving the old provider's last
/// alerts active forever - the job only ever deactivates rows belonging to the
/// provider it just fetched from. And air quality is collapsed into at most one
/// alert: a per-hour list of an index nobody reads hour by hour is noise, while
/// "the air is bad, and it gets worse at 6pm" is the whole message.
/// </summary>
public class HazardService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISiteSettingsService siteSettings,
    TimeProvider time) : IHazardService
{
    /// <summary>The id of the one air quality alert, which is a constant because there is never more than one of them in a snapshot.</summary>
    public const string AirQualityAlertId = "air-quality";

    /// <summary>
    /// How stale the newest sample may be and still count as "now". Samples
    /// are hourly and the job runs four times an hour, so three hours means
    /// the air quality feed has been down for a while - at which point the
    /// honest thing on the wall is nothing, not a reading from this morning.
    /// </summary>
    private static readonly TimeSpan CurrentSampleMaxAge = TimeSpan.FromHours(3);

    /// <summary>How far ahead a forecast peak is worth warning about, matching the window OpenMeteoAirQualityProvider picks its peak from.</summary>
    private static readonly TimeSpan PeakWindow = TimeSpan.FromHours(24);

    public async Task<IReadOnlyList<HazardAlert>> GetAlertsAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var now = time.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var alerts = new List<HazardAlert>();
        alerts.AddRange(await WeatherAlertsAsync(db, settings, now, ct));
        if (await AirQualityAlertAsync(db, settings, now, ct) is { } air) alerts.Add(air);

        return alerts;
    }

    private static async Task<IReadOnlyList<HazardAlert>> WeatherAlertsAsync(
        AppDbContext db, SiteSettingsSnapshot settings, DateTimeOffset now, CancellationToken ct)
    {
        if (Source(settings.WeatherAlertProvider) is not { } source) return [];

        // The same horizon the provider fetched against: an alert that starts
        // on Thursday is not what a Tuesday glance is for.
        var horizon = now.AddHours(settings.HazardMaxSeverityAgeHours);

        var rows = await db.WeatherAlerts.AsNoTracking()
            .Where(a => a.Active && a.Source.ToLower() == source)
            // Expiry is re-checked on read as well as at sync, so an alert
            // ends on the minute it ends rather than at the next firing.
            .Where(a => a.Ends == null || a.Ends > now)
            .Where(a => a.Onset == null || a.Onset <= horizon)
            .ToListAsync(ct);

        return
        [
            .. rows
                .OrderByDescending(a => a.Severity)
                // A null onset means "already in effect", which is ahead of
                // anything still to come at the same severity.
                .ThenBy(a => a.Onset ?? DateTimeOffset.MinValue)
                .ThenBy(a => a.Event, StringComparer.OrdinalIgnoreCase)
                .Select(a => new HazardAlert(
                    a.Id.ToString(),
                    HazardKind.Weather,
                    a.Severity.ToString(),
                    a.Event,
                    // The headline is one sentence written for a person; the
                    // description is paragraphs written for a broadcast feed,
                    // and only one of the two belongs on a wall display.
                    a.Headline,
                    a.Onset,
                    a.Ends)),
        ];
    }

    /// <summary>
    /// At most one alert, and only when the air is actually news: the current
    /// reading or the coming day's peak has to reach
    /// AirQualityAlertThresholdAqi, which defaults to the bottom of "Unhealthy
    /// for Sensitive Groups". Below that, a number on the wall is clutter.
    /// </summary>
    private static async Task<HazardAlert?> AirQualityAlertAsync(
        AppDbContext db, SiteSettingsSnapshot settings, DateTimeOffset now, CancellationToken ct)
    {
        if (Source(settings.AirQualityProvider) is not { } source) return null;

        var current = await db.AirQualitySamples.AsNoTracking()
            .Where(s => s.Source.ToLower() == source)
            .Where(s => s.Timestamp <= now && s.Timestamp >= now - CurrentSampleMaxAge)
            .OrderByDescending(s => s.Timestamp)
            .FirstOrDefaultAsync(ct);

        // No recent sample is not the same as clean air, so it produces no
        // alert rather than a reassuring one.
        if (current is null) return null;

        var peak = await db.AirQualitySamples.AsNoTracking()
            .Where(s => s.Source.ToLower() == source)
            .Where(s => s.Timestamp > now && s.Timestamp <= now + PeakWindow)
            .OrderByDescending(s => s.UsAqi)
            .ThenBy(s => s.Timestamp)
            .FirstOrDefaultAsync(ct);

        var threshold = settings.AirQualityAlertThresholdAqi;
        var peakIsWorse = peak is not null && peak.UsAqi > current.UsAqi;
        var worst = peakIsWorse ? peak!.UsAqi : current.UsAqi;
        if (worst < threshold) return null;

        var band = AirQualityBands.Of(worst);
        return new HazardAlert(
            AirQualityAlertId,
            HazardKind.AirQuality,
            SeverityOf(band).ToString(),
            AirQualityBands.NameOf(band),
            // The number, and the fact that it is still climbing - when it is.
            // The hour it arrives is StartsAt rather than words, because the
            // client owns formatting a time in the house's zone.
            peakIsWorse ? $"US AQI {current.UsAqi} now, rising to {peak!.UsAqi}" : $"US AQI {current.UsAqi}",
            peakIsWorse ? peak!.Timestamp : null,
            null);
    }

    /// <summary>
    /// Bad air on the same severity scale weather alerts use, so the kiosk has
    /// one vocabulary to style against. The bands below the default threshold
    /// still map: an operator who lowers the threshold gets quiet alerts rather
    /// than unstyled ones.
    /// </summary>
    private static WeatherAlertSeverity SeverityOf(AirQualityBand band) => band switch
    {
        AirQualityBand.Good or AirQualityBand.Moderate => WeatherAlertSeverity.Minor,
        AirQualityBand.UnhealthyForSensitiveGroups => WeatherAlertSeverity.Moderate,
        AirQualityBand.Unhealthy => WeatherAlertSeverity.Severe,
        _ => WeatherAlertSeverity.Extreme,
    };

    /// <summary>
    /// The provider setting as it is stored on a row, or null when that half
    /// is off. Lowercased because the setting is free text an admin types
    /// ("NWS") while the rows carry the provider's own lowercase Name.
    /// </summary>
    private static string? Source(string configured)
    {
        var name = configured.Trim().ToLowerInvariant();
        return name.Length == 0 || name == HazardProviders.None ? null : name;
    }
}
