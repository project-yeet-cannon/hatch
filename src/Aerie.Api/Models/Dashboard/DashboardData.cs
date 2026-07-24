namespace Aerie.Api.Models.Dashboard;

// These records are the server side of the dashboard's data contract. They must
// serialize to exactly the shape in Aerie.Web/apps/dashboard/src/types.ts (camelCase
// keys, ISO-8601 timestamps - both the System.Text.Json defaults). Only raw
// physical quantities live here; all presentation (status words, badge text) is
// derived on the client. Nullable numeric fields (CurrentTempF, HumidityPct,
// Low, High) mean "no reading yet" - distinct from a real 0 - so the client
// can render a placeholder instead of a false zero.

public record TempPoint(DateTimeOffset Time, decimal TempF);

public record ComfortRange(decimal LowF, decimal HighF);

public record DailyExtreme(decimal TempF, DateTimeOffset Time);

public record ZoneClimate(
    string Id,
    string Name,
    decimal? CurrentTempF,
    ComfortRange ComfortRange,
    IReadOnlyList<TempPoint> History,
    IReadOnlyList<TempPoint> Forecast,
    DailyExtreme? Low,
    DailyExtreme? High);

public record Precipitation(decimal AmountIn, string Window);

public record HourlyOutside(
    DateTimeOffset Time,
    decimal HumidityPct,
    decimal CloudCoverPct,
    decimal PrecipIn);

public record OutsideClimate(
    decimal? CurrentTempF,
    decimal? HumidityPct,
    decimal SunHoursRemaining,
    DateTimeOffset SunsetTime,
    Precipitation Precipitation,
    string Note,
    IReadOnlyList<TempPoint> History,
    IReadOnlyList<TempPoint> Forecast,
    IReadOnlyList<HourlyOutside> Hourly);

public record DashboardData(
    DateTimeOffset GeneratedAt,
    string Timezone,
    IReadOnlyList<ZoneClimate> Zones,
    OutsideClimate Outside);
