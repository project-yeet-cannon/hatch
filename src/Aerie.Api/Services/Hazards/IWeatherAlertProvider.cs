using Aerie.Api.Ef;

namespace Aerie.Api.Services.Hazards;

/// <summary>
/// One weather alert in Aerie's vocabulary rather than any provider's.
///
/// This record is the mitigation for the fact that the only provider that
/// exists is US-only (docs/plans/kiosk.md, accepted risk 4): everything
/// NWS-shaped - `messageType`, `status`, its severity spellings, its GeoJSON
/// envelope - is translated here and goes no further. A provider for another
/// country is a new class producing these, and nothing downstream changes.
/// </summary>
/// <param name="ProviderAlertId">The provider's own id, unique within that provider. What the sync job upserts on.</param>
/// <param name="Event">What is being warned about, in a form a person reads: "Winter Storm Warning".</param>
/// <param name="Onset">When it takes effect; null reads as "already in effect".</param>
/// <param name="Ends">When it stops applying; null means the provider gave no end.</param>
public record WeatherAlertRecord(
    string ProviderAlertId,
    string Event,
    string? Headline,
    string? Description,
    string? Instruction,
    WeatherAlertSeverity Severity,
    DateTimeOffset? Onset,
    DateTimeOffset? Ends,
    string? AreaDescription);

/// <summary>
/// A source of weather watches, warnings, and advisories for a point. Selected
/// by the WeatherAlertProvider site setting matching <see cref="IHazardProvider.Name"/>,
/// so a non-US operator adds a class and flips a setting - no contract, job, or
/// UI change.
/// </summary>
public interface IWeatherAlertProvider : IHazardProvider
{
    /// <summary>
    /// Every alert currently in effect for the point. Fail-soft, like every
    /// other outbound call here: an unreachable or throttling provider returns
    /// an empty list rather than throwing, since a dead upstream must degrade
    /// one section of the dashboard instead of the whole thing.
    /// </summary>
    Task<IReadOnlyList<WeatherAlertRecord>> GetActiveAlertsAsync(
        double latitude, double longitude, CancellationToken ct);
}
