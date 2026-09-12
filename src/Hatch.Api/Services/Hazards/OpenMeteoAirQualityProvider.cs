using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hatch.Api.Ef;
using Microsoft.AspNetCore.WebUtilities;

namespace Hatch.Api.Services.Hazards;

/// <summary>
/// Air quality from air-quality-api.open-meteo.com - keyless and global, which
/// is why it is the default. Everything Open-Meteo-shaped stops here: its
/// snake_case field names, its offset-less local timestamps, and the fact that
/// "current" and "hourly" are parallel arrays rather than objects.
///
/// Fail-soft like NwsAlertProvider: an unreachable or unparseable upstream
/// returns null, which leaves the air quality half of the hazard panel showing
/// whatever was last cached rather than taking the sync down.
/// </summary>
public class OpenMeteoAirQualityProvider(
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<OpenMeteoAirQualityProvider> logger) : IAirQualityProvider
{
    /// <summary>Named for the role rather than the vendor, so a second global provider shares the client and its timeout.</summary>
    public const string HttpClientName = "AirQuality";

    public const string AirQualityEndpoint = "https://air-quality-api.open-meteo.com/v1/air-quality";

    /// <summary>
    /// How far ahead the peak is looked for. A day, because "today and
    /// tomorrow" is what the kiosk is answering and a smoky evening is exactly
    /// the thing a current reading hides.
    /// </summary>
    private static readonly TimeSpan PeakWindow = TimeSpan.FromHours(24);

    public string Name => HazardProviders.OpenMeteo;

    public async Task<AirQualityReading?> GetCurrentAsync(double latitude, double longitude, CancellationToken ct)
    {
        var url = QueryHelpers.AddQueryString(AirQualityEndpoint, new Dictionary<string, string?>
        {
            ["latitude"] = Coordinate(latitude),
            ["longitude"] = Coordinate(longitude),
            ["current"] = "us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide",
            ["hourly"] = "us_aqi",
            // Two days: today's hours are what "now" is read from and
            // tomorrow's are what makes a 24-hour peak reachable from any hour
            // of the day.
            ["forecast_days"] = "2",
            // Timestamps come back without an offset, so asking for UTC is
            // what makes them unambiguous - see Instant below.
            ["timezone"] = "UTC",
        });

        AirQualityResponse? body;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "air-quality-api.open-meteo.com rejected the query for {Latitude},{Longitude} with {Status}; no air quality this cycle",
                    latitude, longitude, (int)response.StatusCode);
                return null;
            }

            body = JsonSerializer.Deserialize<AirQualityResponse>(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "air-quality-api.open-meteo.com could not be reached for {Latitude},{Longitude}; no air quality this cycle", latitude, longitude);
            return null;
        }

        var current = ToCurrent(body?.Current);
        if (current is null)
        {
            // A 200 that carries no index is not an outage worth a stack
            // trace, but it is worth saying: it means the parameters asked for
            // are no longer the ones being answered.
            logger.LogWarning("air-quality-api.open-meteo.com answered for {Latitude},{Longitude} without a current us_aqi; no air quality this cycle", latitude, longitude);
            return null;
        }

        var hourly = ToHourly(body?.Hourly);
        var now = time.GetUtcNow();

        // The peak is the worst hour still ahead, and the current reading is a
        // candidate for it: when the forecast only improves from here, "the
        // worst it is about to get" is right now.
        var peak = hourly
            .Where(h => h.Timestamp > now && h.Timestamp <= now + PeakWindow)
            .Append(current)
            .MaxBy(h => h.UsAqi)!;

        return new AirQualityReading(current, peak, hourly);
    }

    /// <summary>The current block, or null when it carries no index - the one field without which there is no reading.</summary>
    private static AirQualitySampleRecord? ToCurrent(CurrentBlock? current) =>
        current is { UsAqi: { } usAqi } && Instant(current.Time) is { } at
            ? new AirQualitySampleRecord(at, (int)Math.Round(usAqi), current.Pm25, current.Pm10, current.Ozone, current.NitrogenDioxide)
            : null;

    /// <summary>
    /// The hourly block's parallel arrays zipped back into rows. An hour whose
    /// index is null is dropped rather than read as zero - Open-Meteo pads the
    /// series with nulls where it has no value, and a zero AQI would be a
    /// clean-air reading that never happened.
    /// </summary>
    private static IReadOnlyList<AirQualitySampleRecord> ToHourly(HourlyBlock? hourly)
    {
        if (hourly?.Time is not { } times || hourly.UsAqi is not { } values) return [];

        return [.. times
            .Zip(values, (t, v) => (Time: Instant(t), Value: v))
            .Where(h => h is { Time: not null, Value: not null })
            .Select(h => new AirQualitySampleRecord(h.Time!.Value, (int)Math.Round(h.Value!.Value)))];
    }

    /// <summary>
    /// Open-Meteo sends "2026-08-21T16:00" with no offset, and the request
    /// asked for UTC - so it is read as UTC rather than as the API host's
    /// local time, which is what DateTimeOffset.TryParse would otherwise
    /// assume and would shift every sample by the server's offset.
    /// </summary>
    private static DateTimeOffset? Instant(string? value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : null;

    /// <summary>Invariant-formatted so a European locale cannot send a comma where the API expects a decimal point.</summary>
    private static string Coordinate(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private record AirQualityResponse(
        [property: JsonPropertyName("current")] CurrentBlock? Current,
        [property: JsonPropertyName("hourly")] HourlyBlock? Hourly);

    /// <summary>
    /// The index is a double on the wire even though the scale is defined in
    /// whole numbers, so it is taken as one and rounded rather than failing to
    /// bind on "42.0".
    /// </summary>
    private record CurrentBlock(
        [property: JsonPropertyName("time")] string? Time,
        [property: JsonPropertyName("us_aqi")] double? UsAqi,
        [property: JsonPropertyName("pm2_5")] decimal? Pm25,
        [property: JsonPropertyName("pm10")] decimal? Pm10,
        [property: JsonPropertyName("ozone")] decimal? Ozone,
        [property: JsonPropertyName("nitrogen_dioxide")] decimal? NitrogenDioxide);

    /// <summary>Parallel arrays, index-aligned - Open-Meteo's shape for every series it returns.</summary>
    private record HourlyBlock(
        [property: JsonPropertyName("time")] List<string>? Time,
        [property: JsonPropertyName("us_aqi")] List<double?>? UsAqi);
}
