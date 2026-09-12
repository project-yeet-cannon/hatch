using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hatch.Api.Ef;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.WebUtilities;

namespace Hatch.Api.Services.Hazards;

/// <summary>
/// Watches, warnings, and advisories from api.weather.gov - keyless, and
/// US-only, which is the whole reason IWeatherAlertProvider exists
/// (docs/kiosk-architecture.md). Every NWS-shaped idea stops here:
/// the GeoJSON envelope, `status`, `messageType`, and its severity spellings
/// are all resolved into WeatherAlertRecord and go no further.
///
/// Fail-soft like GoogleCalendarClient and WeatherService: a throttling,
/// unreachable, or unparseable NWS returns an empty list. The consequence is
/// that the kiosk shows no alerts, which is the same thing a calm day looks
/// like - deliberately, since the alternative is a sync job that throws.
/// </summary>
public class NwsAlertProvider(
    IHttpClientFactory httpClientFactory,
    ISiteSettingsService settings,
    TimeProvider time,
    ILogger<NwsAlertProvider> logger) : IWeatherAlertProvider
{
    /// <summary>Named for the role rather than the vendor, so a second country's provider shares the client and its timeout.</summary>
    public const string HttpClientName = "WeatherAlerts";

    public const string AlertsEndpoint = "https://api.weather.gov/alerts/active";

    /// <summary>
    /// NWS 301-redirects a point carrying more precision than this, so the
    /// coordinates are rounded before they are asked about rather than after -
    /// one request instead of two.
    /// </summary>
    private const int PointDecimals = 4;

    /// <summary>
    /// The alert NWS issues to retract one it already sent. It arrives as a
    /// normal feature, so taking it at face value would show the house a
    /// warning that was called off.
    /// </summary>
    private const string CancelMessageType = "Cancel";

    /// <summary>
    /// The only status that describes actual weather. NWS also emits Test,
    /// Exercise, System, and Draft on the live feed, and every one of them
    /// would otherwise reach the wall.
    /// </summary>
    private const string ActualStatus = "Actual";

    // Set once the anonymous User-Agent has been logged. This runs on a
    // 15-minute timer, so without the latch a missing contact setting would
    // warn ninety-six times a day about a condition that does not change.
    private int warnedAboutMissingContact;

    public string Name => HazardProviders.Nws;

    public async Task<IReadOnlyList<WeatherAlertRecord>> GetActiveAlertsAsync(
        double latitude, double longitude, CancellationToken ct)
    {
        var snapshot = await settings.GetAsync(ct);
        var url = QueryHelpers.AddQueryString(AlertsEndpoint, "point", Point(latitude, longitude));

        AlertFeatureCollection? body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // Mandatory: NWS answers an anonymous request with 403. It is a
            // header rather than a client-wide default because the named
            // client is shared and the contact comes from a setting that can
            // change between two calls.
            request.Headers.Add("User-Agent", UserAgent(snapshot.WeatherAlertContact));
            request.Headers.Add("Accept", "application/geo+json");

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "api.weather.gov rejected the alert query for {Point} with {Status}; no weather alerts this cycle",
                    Point(latitude, longitude), (int)response.StatusCode);
                return [];
            }

            body = JsonSerializer.Deserialize<AlertFeatureCollection>(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "api.weather.gov could not be reached for {Point}; no weather alerts this cycle", Point(latitude, longitude));
            return [];
        }

        var now = time.GetUtcNow();

        // The window the kiosk is asking about. An alert that starts on
        // Thursday is not what a Tuesday glance is for, and NWS will happily
        // return one.
        var horizon = now.AddHours(snapshot.HazardMaxSeverityAgeHours);

        return [.. (body?.Features ?? [])
            .Select(f => f.Properties)
            .Where(p => Applies(p, now, horizon))
            .Select(p => ToRecord(p!))
            .OfType<WeatherAlertRecord>()];
    }

    /// <summary>
    /// Whether an alert is one this house should be shown right now: real
    /// rather than a test, not retracted, not already over, and not so far out
    /// that it belongs to a different day.
    /// </summary>
    private static bool Applies(AlertProperties? p, DateTimeOffset now, DateTimeOffset horizon)
    {
        if (p is null) return false;

        if (!ActualStatus.Equals(p.Status, StringComparison.OrdinalIgnoreCase)) return false;
        if (CancelMessageType.Equals(p.MessageType, StringComparison.OrdinalIgnoreCase)) return false;

        // `ends` is when the weather stops; `expires` is when the *bulletin*
        // goes stale and is the only one of the two NWS always sends. Taking
        // ends first keeps a long warning from being dropped just because its
        // hourly refresh is due.
        if ((Instant(p.Ends) ?? Instant(p.Expires)) is { } over && over <= now) return false;

        // A null onset reads as "already in effect", which is the common case:
        // NWS omits it on alerts that began when they were issued.
        if (Instant(p.Onset) is { } starts && starts > horizon) return false;

        return true;
    }

    /// <summary>Null for an alert with no id or no event - there is nothing to upsert on or to put on the wall, and dropping one beats failing the fetch.</summary>
    private static WeatherAlertRecord? ToRecord(AlertProperties p) =>
        NullIfEmpty(p.Id) is { } id && NullIfEmpty(p.Event) is { } name
            ? new WeatherAlertRecord(
                id,
                name,
                NullIfEmpty(p.Headline),
                NullIfEmpty(p.Description),
                NullIfEmpty(p.Instruction),
                ToSeverity(p.Severity),
                Instant(p.Onset),
                Instant(p.Ends) ?? Instant(p.Expires),
                NullIfEmpty(p.AreaDesc))
            : null;

    /// <summary>
    /// NWS's severity words, with anything unrecognized landing on Unknown
    /// rather than throwing. The vocabulary is CAP's and NWS extends it; an
    /// alert whose severity Hatch cannot read is still an alert worth showing.
    /// </summary>
    private static WeatherAlertSeverity ToSeverity(string? severity) => severity?.Trim().ToLowerInvariant() switch
    {
        "minor" => WeatherAlertSeverity.Minor,
        "moderate" => WeatherAlertSeverity.Moderate,
        "severe" => WeatherAlertSeverity.Severe,
        "extreme" => WeatherAlertSeverity.Extreme,
        _ => WeatherAlertSeverity.Unknown,
    };

    /// <summary>
    /// NWS asks callers to identify themselves and documents that anonymous
    /// traffic may be throttled or blocked, so a missing contact is worth
    /// saying out loud once - but not worth refusing to fetch over, since the
    /// keyless default is what lets a fresh install show alerts at all.
    /// </summary>
    private string UserAgent(string? contact)
    {
        if (NullIfEmpty(contact) is { } identified) return $"Hatch/1.0 ({identified})";

        if (Interlocked.Exchange(ref warnedAboutMissingContact, 1) == 0)
        {
            logger.LogWarning(
                "No {SettingKey} is set, so api.weather.gov is being called anonymously; NWS may throttle or block traffic it cannot identify",
                SiteSettingKeys.WeatherAlertContact);
        }

        return "Hatch/1.0 (self-hosted)";
    }

    /// <summary>The `point` NWS expects, invariant-formatted so a European locale cannot turn the decimal separator into the comma that separates the two coordinates.</summary>
    private static string Point(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Math.Round(latitude, PointDecimals)},{Math.Round(longitude, PointDecimals)}");

    /// <summary>Null for a missing or unparseable timestamp, which the callers already treat as "the provider didn't say".</summary>
    private static DateTimeOffset? Instant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private record AlertFeatureCollection(
        [property: JsonPropertyName("features")] List<AlertFeature>? Features);

    private record AlertFeature(
        [property: JsonPropertyName("properties")] AlertProperties? Properties);

    /// <summary>
    /// The properties Hatch reads off a feature. Timestamps stay strings until
    /// Instant looks at them: NWS sends ISO-8601 with an offset, and a field
    /// it sends malformed must cost that one field rather than the whole page.
    /// </summary>
    private record AlertProperties(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("event")] string? Event,
        [property: JsonPropertyName("headline")] string? Headline,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("instruction")] string? Instruction,
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("onset")] string? Onset,
        [property: JsonPropertyName("ends")] string? Ends,
        [property: JsonPropertyName("expires")] string? Expires,
        [property: JsonPropertyName("areaDesc")] string? AreaDesc,
        [property: JsonPropertyName("messageType")] string? MessageType,
        [property: JsonPropertyName("status")] string? Status);
}
