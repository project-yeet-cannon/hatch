namespace Aerie.Api.Services.Dashboard;

/// <summary>
/// No longer bound from configuration (the "Dashboard" appsettings section was
/// removed in Phase 6 of docs/device-architecture.md) - only survives as the
/// set of fallback defaults DeviceMappingSeeder writes into SiteSetting on a
/// fresh, empty database. Entity-id fields (OutsideTemperatureEntity/
/// OutsideHumidityEntity) were dropped since those now live on Zone/Device/
/// DeviceChannel rows instead.
/// </summary>
public class DashboardOptions
{
    /// <summary>IANA timezone the dashboard displays in.</summary>
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>Home Assistant weather entity id, e.g. "weather.forecast_home". Source of the outside card's condition note; empty leaves it blank.</summary>
    public string? WeatherEntity { get; set; }

    /// <summary>Site latitude/longitude in degrees, used to calculate sunset locally (SolarCalculator). Defaults to New York City, matching the TimeZone default.</summary>
    public double Latitude { get; set; } = 40.7128;
    public double Longitude { get; set; } = -74.0060;

    /// <summary>Degrees F either side of the thermostat setpoint used as the comfort band when a zone has no configured range.</summary>
    public decimal ComfortToleranceF { get; set; } = 2m;

    /// <summary>Comfort band used when a zone has neither a configured range nor a known setpoint.</summary>
    public decimal DefaultComfortLowF { get; set; } = 68m;
    public decimal DefaultComfortHighF { get; set; } = 72m;
}

/// <summary>The time window and resolution a dashboard snapshot is built over.</summary>
public record DashboardWindow(TimeSpan History, TimeSpan Forecast, TimeSpan Bucket)
{
    /// <summary>Matches what the mock data source produced: 9h back, 7h forward, 30-minute buckets.</summary>
    public static readonly DashboardWindow Default =
        new(TimeSpan.FromHours(9), TimeSpan.FromHours(7), TimeSpan.FromMinutes(30));
}
