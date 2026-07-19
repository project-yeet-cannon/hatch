using System.Globalization;
using System.Linq.Expressions;
using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using HADotNet.Core.Clients;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.Dashboard;

public interface IWeatherService
{
    Task<OutsideClimate> GetOutsideAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// Sources the Outside card's current temperature/humidity and history from
/// EnvironmentReadings (configured via DashboardOptions.OutsideTemperatureEntity/
/// OutsideHumidityEntity), populated by the SampleOutside job polling those
/// sensor.* entities - the same pattern ZoneService uses for climate.* zones.
/// "Current" is just the most recent reading in that table rather than a live
/// HA call, so the dashboard doesn't hit HA on every load. The condition note
/// (weather.* entity) and sunset (sun.sun) aren't sampled and still come from
/// live HA state. This is best-effort by design: if an entity isn't configured
/// or a call/query fails, that piece falls back to zero/blank rather than
/// throwing, so a missing integration degrades the outside card instead of
/// breaking the dashboard. Forecast/hourly aren't sourced yet, so they come
/// back empty - the frontend chart tolerates empty series.
/// </summary>
public class WeatherService(
    StatesClient states,
    IDbContextFactory<AerieContext> dbFactory,
    IOptions<DashboardOptions> options,
    TimeProvider time,
    ILogger<WeatherService> logger) : IWeatherService
{
    private DashboardOptions Opt => options.Value;

    public async Task<OutsideClimate> GetOutsideAsync(DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var from = now - window.History;

        var note = string.Empty;
        if (!string.IsNullOrWhiteSpace(Opt.WeatherEntity))
        {
            try
            {
                var w = await states.GetState(Opt.WeatherEntity);
                note = Humanize(w.State);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read weather entity {Entity}", Opt.WeatherEntity);
            }
        }

        var sunsetTime = now;
        decimal sunHoursRemaining = 0;
        try
        {
            var sun = await states.GetState(Opt.SunEntity);
            if (GetDateTime(sun.Attributes, "next_setting") is { } nextSetting)
            {
                sunsetTime = nextSetting;
                sunHoursRemaining = Math.Round(Math.Max(0m, (decimal)(nextSetting - now).TotalHours), 1);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read sun entity {Entity}", Opt.SunEntity);
        }

        var (currentTempF, history) = await GetTempHistoryAsync(Opt.OutsideTemperatureEntity, from, now, window.Bucket, ct);
        var humidityPct = await GetLatestReadingAsync(Opt.OutsideHumidityEntity, r => r.Humidity, from, now, ct);

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

    /// <summary>
    /// Current temperature + charted history both come from the same
    /// EnvironmentReadings rows SampleOutside polls into, so the "current"
    /// value is the most recent sample rather than a live HA call - see
    /// ZoneService.BuildZoneAsync for the same pattern applied to zones.
    /// </summary>
    private async Task<(decimal CurrentTempF, IReadOnlyList<TempPoint> History)> GetTempHistoryAsync(
        string? entityId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return (0, []);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.EnvironmentReadings
            .Where(r => r.EntityId == entityId && r.Timestamp >= from && r.Timestamp <= to)
            .OrderBy(r => r.Timestamp)
            .Select(r => new { r.Timestamp, r.Temperature })
            .ToListAsync(ct);

        var history = ZoneMath.Bucket(rows.Select(r => (r.Timestamp, r.Temperature)), from, to, bucket);
        var currentTempF = rows.Count > 0 ? rows[^1].Temperature ?? 0 : (history.Count > 0 ? history[^1].TempF : 0m);

        return (currentTempF, history);
    }

    private async Task<decimal> GetLatestReadingAsync(
        string? entityId, Expression<Func<EfEnvironmentReading, decimal?>> selector,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return 0;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var latest = await db.EnvironmentReadings
            .Where(r => r.EntityId == entityId && r.Timestamp >= from && r.Timestamp <= to)
            .OrderByDescending(r => r.Timestamp)
            .Select(selector)
            .FirstOrDefaultAsync(ct);

        return latest ?? 0;
    }

    private static DateTimeOffset? GetDateTime(IDictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var v) || v is null) return null;
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
