using System.Globalization;
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
/// Sources the Outside card's current temperature/humidity from Home
/// Assistant's outdoor sensor.* entities (configured via
/// DashboardOptions.OutsideTemperatureEntity/OutsideHumidityEntity), the
/// condition note from a weather.* entity, and sunset from sun.sun. This is
/// best-effort by design: if an entity isn't configured or a call fails, that
/// piece falls back to zero/blank rather than throwing, so a missing
/// integration degrades the outside card instead of breaking the dashboard.
///
/// History comes from EnvironmentReadings, populated by the SampleOutside job
/// polling the same sensor entities - the same pattern ZoneService uses for
/// climate.* zones. Forecast/hourly aren't sourced yet, so they come back
/// empty - the frontend chart tolerates empty series.
/// </summary>
public class WeatherService(
    StatesClient states,
    AerieContext db,
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

        var currentTempF = await GetCurrentSensorValue(Opt.OutsideTemperatureEntity);
        var humidityPct = await GetCurrentSensorValue(Opt.OutsideHumidityEntity);

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

        var history = await GetHistoryAsync(Opt.OutsideTemperatureEntity, from, now, window.Bucket, ct);

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

    private async Task<decimal> GetCurrentSensorValue(string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return 0;
        try
        {
            var s = await states.GetState(entityId);
            return ParseDecimal(s.State) ?? 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read sensor entity {Entity}", entityId);
            return 0;
        }
    }

    private async Task<IReadOnlyList<TempPoint>> GetHistoryAsync(
        string? entityId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return [];

        var rows = await db.EnvironmentReadings
            .Where(r => r.EntityId == entityId && r.Timestamp >= from && r.Timestamp <= to)
            .OrderBy(r => r.Timestamp)
            .Select(r => new { r.Timestamp, r.Temperature })
            .ToListAsync(ct);

        return ZoneMath.Bucket(rows.Select(r => (r.Timestamp, r.Temperature)), from, to, bucket);
    }

    private static decimal? ParseDecimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

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
