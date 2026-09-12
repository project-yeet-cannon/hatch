using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Ef;

/// <summary>
/// The values EfWeatherAlert.Source and EfAirQualitySample.Source take, which
/// are also the values the WeatherAlertProvider / AirQualityProvider settings
/// take - one spelling, shared by the row and the setting that produced it.
/// Same shape as CalendarProviders, and for the same reason: a second provider
/// is a new constant, not a migration.
/// </summary>
public static class HazardProviders
{
    /// <summary>api.weather.gov - keyless, US-only. The weather alert default.</summary>
    public const string Nws = "nws";

    /// <summary>air-quality-api.open-meteo.com - keyless, global. The air quality default.</summary>
    public const string OpenMeteo = "open-meteo";

    /// <summary>Not a provider: the setting value that turns one half of the hazard feature off entirely.</summary>
    public const string None = "none";
}

// New values must be appended at the end - the column stores the enum's
// underlying int, so inserting elsewhere would silently remap every existing
// row's Severity to the wrong value (same rule as DeviceChannelMetric).
//
// Unknown is first and is the fallback on purpose: a provider that invents a
// severity word Hatch has not seen should land here rather than throw, and an
// alert whose severity we cannot read is still an alert worth showing.
public enum WeatherAlertSeverity { Unknown, Minor, Moderate, Severe, Extreme }

/// <summary>
/// One weather advisory, watch, or warning as some IWeatherAlertProvider
/// reported it. Written by the hazard sync job, read by the dashboard BFF -
/// nothing in the request path calls the provider (docs/kiosk-architecture.md).
///
/// Every column here is provider-neutral by design. NWS is US-only, so the
/// day a second provider exists this table has to already fit it: no `event`,
/// `messageType`, or `status` vocabulary from api.weather.gov survives past
/// NwsAlertProvider.
/// </summary>
[Table("WeatherAlerts")]
[Index(nameof(Source), nameof(ProviderAlertId), IsUnique = true)]
public class EfWeatherAlert
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>The provider that produced this row, e.g. "nws" - see HazardProviders. Part of the natural key, so switching providers cannot collide two alerts that happen to share an id.</summary>
    public required string Source { get; set; }

    /// <summary>The provider's own id for the alert, which is what the sync job upserts on.</summary>
    public required string ProviderAlertId { get; set; }

    /// <summary>What is being warned about, in the provider's own words: "Winter Storm Warning", "Air Quality Alert". The one line the kiosk always shows.</summary>
    public required string Event { get; set; }

    /// <summary>The provider's one-sentence summary, when it has one.</summary>
    public string? Headline { get; set; }

    /// <summary>The full narrative text. Far too long for the wall display, kept because the admin-facing view can show it.</summary>
    public string? Description { get; set; }

    /// <summary>What the issuing office says to actually do about it, when it says anything.</summary>
    public string? Instruction { get; set; }

    public WeatherAlertSeverity Severity { get; set; }

    /// <summary>When the alert takes effect. Null when the provider gives no onset, which reads as "already in effect".</summary>
    public DateTimeOffset? Onset { get; set; }

    /// <summary>When the alert stops applying. Null means open-ended, so age is judged from FetchedAt instead.</summary>
    public DateTimeOffset? Ends { get; set; }

    /// <summary>The area the alert covers, as the provider describes it ("Boulder County"). Informational - the point query already decided this alert applies here.</summary>
    public string? AreaDescription { get; set; }

    /// <summary>
    /// False once the provider stops returning the alert. The row stays: "what
    /// was the house warned about last night" is worth keeping, and these rows
    /// are tiny. Only Active rows reach the dashboard.
    /// </summary>
    public bool Active { get; set; } = true;

    public required DateTimeOffset FetchedAt { get; set; }
}

/// <summary>
/// One hourly air quality observation or forecast from an IAirQualityProvider.
///
/// Stored per hour rather than as a single current value so a chart over the
/// day is possible later without a second migration - the same bet
/// EfMeasurement makes about sensor history.
/// </summary>
[Table("AirQualitySamples")]
[Index(nameof(Source), nameof(Timestamp), IsUnique = true)]
public class EfAirQualitySample
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>The provider that produced this row, e.g. "open-meteo" - see HazardProviders.</summary>
    public required string Source { get; set; }

    /// <summary>The hour this sample describes. Unique with Source, which makes re-fetching an hour already stored a no-op rather than a duplicate.</summary>
    public required DateTimeOffset Timestamp { get; set; }

    /// <summary>US AQI, the index the bands and the alert threshold are both expressed in. An integer because the scale is defined as one.</summary>
    public int UsAqi { get; set; }

    /// <summary>Component pollutants, in the provider's units (ug/m3 for particulates, ug/m3 for the gases as Open-Meteo reports them). Null when the provider did not report that pollutant - kept so the AQI number has something behind it.</summary>
    public decimal? Pm25 { get; set; }
    public decimal? Pm10 { get; set; }
    public decimal? Ozone { get; set; }
    public decimal? No2 { get; set; }

    public required DateTimeOffset FetchedAt { get; set; }
}
