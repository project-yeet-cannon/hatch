namespace Aerie.Api.Services.Dashboard;

/// <summary>Bound from the "Dashboard" section of configuration.</summary>
public class DashboardOptions
{
    /// <summary>IANA timezone the dashboard displays in.</summary>
    public string TimeZone { get; set; } = "America/New_York";

    /// <summary>Home Assistant weather entity id, e.g. "weather.forecast_home". Source of the outside card's condition note; empty leaves it blank.</summary>
    public string? WeatherEntity { get; set; }

    /// <summary>Home Assistant sun entity id, source of sunset time.</summary>
    public string SunEntity { get; set; } = "sun.sun";

    /// <summary>Home Assistant sensor entity id for outdoor temperature, e.g. "sensor.h5110_716d_temperature". Empty disables the outside card's live temperature.</summary>
    public string? OutsideTemperatureEntity { get; set; }

    /// <summary>Home Assistant sensor entity id for outdoor humidity, e.g. "sensor.h5110_716d_humidity". Empty disables the outside card's live humidity.</summary>
    public string? OutsideHumidityEntity { get; set; }

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
