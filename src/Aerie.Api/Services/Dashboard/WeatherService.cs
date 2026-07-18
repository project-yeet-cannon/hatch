using System.Globalization;
using Aerie.Api.Models.Dashboard;
using HADotNet.Core.Clients;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.Dashboard;

public interface IWeatherService
{
    Task<OutsideClimate> GetOutsideAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// Sources the Outside card from Home Assistant's weather + sun entities. This
/// is best-effort by design: if the entities aren't configured or a call fails,
/// it returns a zeroed OutsideClimate rather than throwing, so a missing weather
/// integration degrades the outside card instead of breaking the dashboard.
///
/// Outdoor temperature *history* isn't stored yet (the sampler only records
/// climate.* entities), so history/forecast/hourly come back empty for now - the
/// frontend chart tolerates empty series. Persisting the weather entity through
/// the existing sampler is the natural next step to fill them in.
/// </summary>
public class WeatherService(
    StatesClient states,
    IOptions<DashboardOptions> options,
    TimeProvider time,
    ILogger<WeatherService> logger) : IWeatherService
{
    private DashboardOptions Opt => options.Value;

    public async Task<OutsideClimate> GetOutsideAsync(DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        decimal currentTempF = 0, humidityPct = 0;
        var note = string.Empty;

        if (!string.IsNullOrWhiteSpace(Opt.WeatherEntity))
        {
            try
            {
                var w = await states.GetState(Opt.WeatherEntity);
                currentTempF = GetDecimal(w.Attributes, "temperature") ?? 0;
                humidityPct = GetDecimal(w.Attributes, "humidity") ?? 0;
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

        return new OutsideClimate(
            CurrentTempF: currentTempF,
            HumidityPct: humidityPct,
            SunHoursRemaining: sunHoursRemaining,
            SunsetTime: sunsetTime,
            Precipitation: new Precipitation(0, string.Empty),
            Note: note,
            History: [],
            Forecast: [],
            Hourly: []);
    }

    private static decimal? GetDecimal(IDictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            long l => l,
            int i => i,
            double d => (decimal)d,
            decimal m => m,
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) => r,
            _ => null,
        };
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
