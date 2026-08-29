using System.Globalization;
using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.DeviceMapping;
using HADotNet.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Dashboard;

public interface IWeatherService
{
    Task<OutsideClimate> GetOutsideAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// Sources the Outside card's current temperature/humidity and history from
/// the Zone(Kind = Outside)'s devices - whichever hygrometer(s) an admin has
/// assigned there - via the Measurement rows the channel-driven sampling job
/// (Jobs/SampleChannels) writes (docs/device-architecture.md Phase 5).
/// "Current" is the most recent Measurement rather than a live HA call,
/// matching ZoneService.BuildZoneAsync. The condition note (weather.* entity)
/// isn't sampled and still comes from live HA state. Sunset is computed
/// locally from the site's Latitude/Longitude (SolarCalculator) rather than
/// read from HA's sun.* entity, so it's not subject to HA's availability at
/// all.
///
/// The HA weather call is bounded by HaCallTimeout: StatesClient's HttpClient
/// has no timeout configured (HADotNet just does `new HttpClient()`, so the
/// framework's 100s default applies) and HADotNet has no retry/circuit
/// breaking of its own. Without an explicit bound here, a slow or
/// unreachable HA instance stalls the whole /api/dashboard request - this
/// endpoint's data otherwise comes entirely from Postgres and should be fast
/// regardless of HA's health. This is best-effort by design: if the Outside
/// zone doesn't exist yet or has no devices assigned, or the live HA call
/// fails or times out, that piece falls back to zero/blank rather than
/// throwing, so a missing/slow integration degrades the outside card instead
/// of the whole dashboard. Forecast/hourly aren't sourced yet, so they come
/// back empty - the frontend chart tolerates empty series.
/// </summary>
public class WeatherService(
    IHomeAssistantStateReader states,
    IDbContextFactory<AerieContext> dbFactory,
    ISiteSettingsService siteSettings,
    TimeProvider time,
    ILogger<WeatherService> logger) : IWeatherService
{
    private static readonly TimeSpan HaCallTimeout = TimeSpan.FromSeconds(3);

    public async Task<OutsideClimate> GetOutsideAsync(DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var from = now - window.History;
        var settings = await siteSettings.GetAsync(ct);

        var weatherTask = string.IsNullOrWhiteSpace(settings.WeatherEntity)
            ? Task.FromResult<StateObject?>(null)
            : BoundedStateAsync(settings.WeatherEntity, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zoneTask = db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Kind == ZoneKind.Outside, ct);

        await Task.WhenAll(weatherTask, zoneTask);

        var note = Humanize((await weatherTask)?.State);

        var sunsetTime = SolarCalculator.NextSunset(now, settings.Latitude, settings.Longitude);
        var sunHoursRemaining = Math.Round(Math.Max(0m, (decimal)(sunsetTime - now).TotalHours), 1);

        var zone = await zoneTask;
        (decimal? currentTempF, IReadOnlyList<TempPoint> history, DateTimeOffset? currentAsOf) = zone is null
            ? (null, [], null)
            : await GetTempHistoryAsync(db, zone.Id, from, now, window.Bucket, ct);

        var humidityPct = zone is null
            ? null
            : await ZoneMeasurements.LatestAsync(db, zone.Id, DeviceChannelMetric.Humidity, from, now, ct);

        return new OutsideClimate(
            CurrentTempF: currentTempF,
            HumidityPct: humidityPct,
            SunHoursRemaining: sunHoursRemaining,
            SunsetTime: sunsetTime,
            Precipitation: new Precipitation(0, string.Empty),
            Note: note,
            History: history,
            Forecast: [],
            Hourly: [],
            CurrentAsOf: currentAsOf);
    }

    private async Task<StateObject?> BoundedStateAsync(string entityId, CancellationToken ct)
    {
        try
        {
            return await states.TryGetStateAsync(entityId, ct).WaitAsync(HaCallTimeout, ct);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Home Assistant entity {Entity} did not respond within {Timeout}", entityId, HaCallTimeout);
            return null;
        }
    }

    private static async Task<(decimal? CurrentTempF, IReadOnlyList<TempPoint> History, DateTimeOffset? CurrentAsOf)> GetTempHistoryAsync(
        AerieContext db, Guid zoneId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct)
    {
        var rows = await ZoneMeasurements.ForZone(db, zoneId, DeviceChannelMetric.Temperature, from, to)
            .OrderBy(m => m.Timestamp)
            .Select(m => new { m.Timestamp, m.Value })
            .ToListAsync(ct);

        var history = ZoneMath.Bucket(rows.Select(r => (r.Timestamp, (decimal?)r.Value)), from, to, bucket);
        var currentTempF = rows.Count > 0 ? rows[^1].Value : (history.Count > 0 ? history[^1].TempF : (decimal?)null);
        // Only ever a row's own timestamp - see the same rule in ZoneService.
        // The outside temperature comes down the same road and goes stale the
        // same way, so it gets the same field rather than a different story.
        var currentAsOf = rows.Count > 0 ? rows[^1].Timestamp : (DateTimeOffset?)null;

        return (currentTempF, history, currentAsOf);
    }

    /// <summary>"partlycloudy" -> "Partly Cloudy".</summary>
    private static string Humanize(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return string.Empty;
        var spaced = condition.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced);
    }
}
