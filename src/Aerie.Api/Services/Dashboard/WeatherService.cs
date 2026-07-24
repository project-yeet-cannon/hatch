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
/// and sunset (sun.* entity) aren't sampled and still come from live HA state,
/// fetched concurrently with each other and with the DB reads below since
/// they're independent.
///
/// Each HA call is bounded by HaCallTimeout: StatesClient's HttpClient has no
/// timeout configured (HADotNet just does `new HttpClient()`, so the
/// framework's 100s default applies) and HADotNet has no retry/circuit
/// breaking of its own. Without an explicit bound here, a slow or
/// unreachable HA instance stalls the whole /api/dashboard request - this
/// endpoint's data otherwise comes entirely from Postgres and should be fast
/// regardless of HA's health. This is best-effort by design: if the Outside
/// zone doesn't exist yet or has no devices assigned, or a live HA call
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
        var sunTask = BoundedStateAsync(settings.SunEntity, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zoneTask = db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Kind == ZoneKind.Outside, ct);

        await Task.WhenAll(weatherTask, sunTask, zoneTask);

        var note = Humanize((await weatherTask)?.State);

        var sunsetTime = now;
        decimal sunHoursRemaining = 0;
        if (GetDateTime((await sunTask)?.Attributes, "next_setting") is { } nextSetting)
        {
            sunsetTime = nextSetting;
            sunHoursRemaining = Math.Round(Math.Max(0m, (decimal)(nextSetting - now).TotalHours), 1);
        }

        var zone = await zoneTask;
        (decimal currentTempF, IReadOnlyList<TempPoint> history) = zone is null
            ? (0m, [])
            : await GetTempHistoryAsync(db, zone.Id, from, now, window.Bucket, ct);

        var humidityPct = zone is null
            ? 0m
            : await ZoneMeasurements.LatestAsync(db, zone.Id, DeviceChannelMetric.Humidity, from, now, ct) ?? 0m;

        return new OutsideClimate(
            CurrentTempF: currentTempF,
            HumidityPct: humidityPct,
            SunHoursRemaining: sunHoursRemaining,
            SunsetTime: sunsetTime,
            Precipitation: new Precipitation(0, string.Empty),
            Note: note,
            History: history,
            Forecast: [],
            Hourly: []);
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

    private static async Task<(decimal CurrentTempF, IReadOnlyList<TempPoint> History)> GetTempHistoryAsync(
        AerieContext db, Guid zoneId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct)
    {
        var rows = await ZoneMeasurements.ForZone(db, zoneId, DeviceChannelMetric.Temperature, from, to)
            .OrderBy(m => m.Timestamp)
            .Select(m => new { m.Timestamp, m.Value })
            .ToListAsync(ct);

        var history = ZoneMath.Bucket(rows.Select(r => (r.Timestamp, (decimal?)r.Value)), from, to, bucket);
        var currentTempF = rows.Count > 0 ? rows[^1].Value : (history.Count > 0 ? history[^1].TempF : 0m);

        return (currentTempF, history);
    }

    private static DateTimeOffset? GetDateTime(IDictionary<string, object>? attrs, string key)
    {
        if (attrs is null || !attrs.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => dt,
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var r) => r,
            _ => null,
        };
    }

    /// <summary>"partlycloudy" -> "Partly Cloudy".</summary>
    private static string Humanize(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return string.Empty;
        var spaced = condition.Replace('_', ' ').Replace('-', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced);
    }
}
