namespace Aerie.Api.Services.Hazards;

/// <summary>
/// Air quality at one hour, provider-neutral. Pollutant components are null
/// when the provider did not report them - the hourly series carries the index
/// alone, while the current hour usually carries the components too.
/// </summary>
public record AirQualitySampleRecord(
    DateTimeOffset Timestamp,
    int UsAqi,
    decimal? Pm25 = null,
    decimal? Pm10 = null,
    decimal? Ozone = null,
    decimal? No2 = null);

/// <summary>
/// What one air quality fetch produced: right now, the worst it is about to
/// get, and the hourly series behind both.
///
/// <paramref name="Peak"/> is what makes this cover "today and tomorrow"
/// rather than only this instant - a clean morning ahead of a smoky evening
/// is exactly the case the kiosk exists to catch, and it is invisible in a
/// current reading. It is the same record type as a sample because the peak
/// *is* one of the hours, picked out.
/// </summary>
public record AirQualityReading(
    AirQualitySampleRecord Current,
    AirQualitySampleRecord Peak,
    IReadOnlyList<AirQualitySampleRecord> Hourly);

/// <summary>
/// A source of air quality readings for a point. Selected by the
/// AirQualityProvider site setting matching <see cref="IHazardProvider.Name"/>.
/// </summary>
public interface IAirQualityProvider : IHazardProvider
{
    /// <summary>
    /// The current reading plus the coming day's peak, or null when the
    /// provider could not be reached or answered with something unparseable.
    /// Null rather than an exception: air quality going stale must not take
    /// the rest of the hazard sync down with it.
    /// </summary>
    Task<AirQualityReading?> GetCurrentAsync(
        double latitude, double longitude, CancellationToken ct);
}
