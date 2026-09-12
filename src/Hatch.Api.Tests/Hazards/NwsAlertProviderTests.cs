using System.Net;
using Hatch.Api.Ef;
using Hatch.Api.Services.Hazards;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Hazards;

/// <summary>
/// Covers what NWS sends that must never reach the wall - a cancelled alert, a
/// test message, weather that is already over - plus the two things the
/// provider owes its caller: a severity it can always produce, and an empty
/// list rather than an exception when api.weather.gov says no.
/// </summary>
public class NwsAlertProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MapsAnAlertIntoHatchVocabulary()
    {
        // Captured verbatim from api.weather.gov, so a rename on their side
        // fails here rather than on the wall - including the newlines NWS puts
        // in its narrative text, which survive into the stored description.
        var (provider, _) = NewProvider(Ok("""
            {"properties":{
              "id":"urn:oid:2.49.0.1.840.0.94e090866f5bec33c0320bea284dab820358f871.001.1",
              "areaDesc":"Inland Broward County; Metro Broward County; Inland Miami-Dade County",
              "onset":"2026-08-21T11:00:00-04:00",
              "expires":"2026-08-21T18:00:00-04:00",
              "ends":"2026-08-21T18:00:00-04:00",
              "status":"Actual",
              "messageType":"Alert",
              "category":"Met",
              "severity":"Moderate",
              "certainty":"Likely",
              "urgency":"Expected",
              "event":"Heat Advisory",
              "senderName":"NWS Miami FL",
              "headline":"Heat Advisory issued August 21 at 1:15AM EDT until August 21 at 6:00PM EDT by NWS Miami FL",
              "description":"* WHAT...Heat index values up to 107 expected.\n\n* WHEN...From 11 AM this morning to 6 PM EDT this evening.",
              "instruction":"Drink plenty of fluids, stay in an air-conditioned room."}}
            """));

        var alert = Assert.Single(await provider.GetActiveAlertsAsync(25.7617, -80.1918, CancellationToken.None));

        Assert.Equal("urn:oid:2.49.0.1.840.0.94e090866f5bec33c0320bea284dab820358f871.001.1", alert.ProviderAlertId);
        Assert.Equal("Heat Advisory", alert.Event);
        Assert.Equal("Heat Advisory issued August 21 at 1:15AM EDT until August 21 at 6:00PM EDT by NWS Miami FL", alert.Headline);
        Assert.StartsWith("* WHAT...Heat index values up to 107 expected.", alert.Description);
        Assert.Equal("Drink plenty of fluids, stay in an air-conditioned room.", alert.Instruction);
        Assert.Equal(WeatherAlertSeverity.Moderate, alert.Severity);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 15, 0, 0, TimeSpan.Zero), alert.Onset);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 22, 0, 0, TimeSpan.Zero), alert.Ends);
        Assert.Equal("Inland Broward County; Metro Broward County; Inland Miami-Dade County", alert.AreaDescription);
    }

    [Fact]
    public async Task PrefersTheWeathersEndOverTheBulletinsExpiry()
    {
        // A live Houston advisory ended at 19:00 while its bulletin expired at
        // 07:00 the same morning: `expires` is when NWS will have refreshed
        // the message, not when the heat stops. Taking it first would drop a
        // long advisory twelve hours early.
        var (provider, _) = NewProvider(Ok(Feature(
            "houston", onset: null, ends: "2026-08-22T19:00:00-05:00", expires: "2026-08-22T07:00:00-05:00")));

        var alert = Assert.Single(await provider.GetActiveAlertsAsync(29.7604, -95.3698, CancellationToken.None));

        Assert.Equal(new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero), alert.Ends);
    }

    [Fact]
    public async Task KeepsAnUpdateToAnAlertAlreadyInEffect()
    {
        // Only Cancel retracts. NWS re-sends a live advisory as an Update
        // every refresh, which is the majority of what a busy point returns.
        var (provider, _) = NewProvider(Ok(Feature("updated", messageType: "Update")));

        Assert.Equal("updated", Assert.Single(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None)).ProviderAlertId);
    }

    [Fact]
    public async Task DropsACancelledAlert()
    {
        // NWS retracts by sending another feature, so a Cancel taken at face
        // value would put a called-off warning on the wall.
        var (provider, _) = NewProvider(Ok(Feature("cancelled", messageType: "Cancel"), Feature("live")));

        var alert = Assert.Single(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None));

        Assert.Equal("live", alert.ProviderAlertId);
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("Exercise")]
    [InlineData("Draft")]
    public async Task DropsAnythingThatIsNotActualWeather(string status)
    {
        var (provider, _) = NewProvider(Ok(Feature("drill", status: status)));

        Assert.Empty(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None));
    }

    [Fact]
    public async Task DropsAnAlertThatHasAlreadyEnded()
    {
        var (provider, _) = NewProvider(Ok(Feature("over", ends: "2026-08-21T15:00:00+00:00"), Feature("live")));

        Assert.Equal(["live"], (await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None)).Select(a => a.ProviderAlertId));
    }

    [Fact]
    public async Task FallsBackToExpiresWhenNoEndIsGiven()
    {
        // `expires` is the only one of the two NWS always sends.
        var (provider, _) = NewProvider(Ok(Feature("stale", ends: null, expires: "2026-08-21T15:00:00+00:00")));

        Assert.Empty(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None));
    }

    [Fact]
    public async Task DropsAnAlertThatStartsBeyondTheHazardWindow()
    {
        // 48 hours by default, so a warning that begins in three days is not
        // what a glance at today's kiosk is asking about.
        var (provider, _) = NewProvider(
            Ok(Feature("thursday", onset: "2026-08-24T12:00:00+00:00", ends: "2026-08-24T20:00:00+00:00"), Feature("live")));

        Assert.Equal(["live"], (await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None)).Select(a => a.ProviderAlertId));
    }

    [Fact]
    public async Task AnAlertWithNoOnsetIsAlreadyInEffect()
    {
        var (provider, _) = NewProvider(Ok(Feature("now", onset: null)));

        var alert = Assert.Single(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None));

        Assert.Null(alert.Onset);
    }

    [Theory]
    [InlineData("Minor", WeatherAlertSeverity.Minor)]
    [InlineData("Moderate", WeatherAlertSeverity.Moderate)]
    [InlineData("Severe", WeatherAlertSeverity.Severe)]
    [InlineData("Extreme", WeatherAlertSeverity.Extreme)]
    [InlineData("Unknown", WeatherAlertSeverity.Unknown)]
    public async Task MapsEverySeverityNwsDocuments(string severity, WeatherAlertSeverity expected)
    {
        var (provider, _) = NewProvider(Ok(Feature("a", severity: severity)));

        Assert.Equal(expected, Assert.Single(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None)).Severity);
    }

    [Theory]
    [InlineData("Catastrophic")]
    [InlineData("")]
    public async Task AnUnrecognizedSeverityBecomesUnknownRatherThanDroppingTheAlert(string severity)
    {
        // NWS extends the CAP vocabulary; an alert whose severity Hatch cannot
        // read is still an alert worth showing.
        var (provider, _) = NewProvider(Ok(Feature("a", severity: severity)));

        Assert.Equal(WeatherAlertSeverity.Unknown, Assert.Single(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None)).Severity);
    }

    [Fact]
    public async Task DropsAFeatureWithNoIdOrNoEvent()
    {
        var (provider, _) = NewProvider(Ok("""
            {"properties":{"event":"Nameless","severity":"Severe","messageType":"Alert","status":"Actual"}}
            """, """
            {"properties":{"id":"no-event","severity":"Severe","messageType":"Alert","status":"Actual"}}
            """, Feature("live")));

        Assert.Equal(["live"], (await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None)).Select(a => a.ProviderAlertId));
    }

    [Fact]
    public async Task ACalmDayIsAnEmptyList()
    {
        var (provider, _) = NewProvider(Ok());

        Assert.Empty(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ARejectionReturnsEmptyRatherThanThrowing(HttpStatusCode status)
    {
        // Fail-soft: a throttling NWS costs the hazard panel, not the sync job.
        var (provider, _) = NewProvider(StubHttpMessageHandler.Json(status, """{"correlationId":"nope"}"""));

        Assert.Empty(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None));
    }

    [Fact]
    public async Task UnparseableJsonReturnsEmptyRatherThanThrowing()
    {
        var (provider, _) = NewProvider(StubHttpMessageHandler.Json(HttpStatusCode.OK, "<html>not json</html>"));

        Assert.Empty(await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None));
    }

    [Fact]
    public async Task IdentifiesItselfWithTheConfiguredContact()
    {
        // NWS answers an anonymous request with 403, so this header is not
        // decoration - it is the difference between data and nothing.
        var (provider, handler) = NewProvider(Ok(), contact: "hatch@example.com");

        await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None);

        Assert.Equal("Hatch/1.0 (hatch@example.com)", Assert.Single(handler.Requests).Headers["User-Agent"]);
    }

    [Fact]
    public async Task StillIdentifiesItselfWhenNoContactIsConfigured()
    {
        var (provider, handler) = NewProvider(Ok());

        await provider.GetActiveAlertsAsync(40.0, -105.0, CancellationToken.None);

        Assert.Equal("Hatch/1.0 (self-hosted)", Assert.Single(handler.Requests).Headers["User-Agent"]);
    }

    [Fact]
    public async Task AsksAboutThePointItWasGiven()
    {
        var (provider, handler) = NewProvider(Ok());

        // Over-precise coordinates: NWS 301-redirects rather than answering,
        // so they are rounded before the request instead of after.
        await provider.GetActiveAlertsAsync(39.7392358, -104.990251, CancellationToken.None);

        Assert.Contains("point=39.7392,-104.9903", Assert.Single(handler.Requests).Url);
    }

    /// <summary>A GeoJSON response wrapping the given feature bodies, matching what api.weather.gov returns for a point query.</summary>
    private static HttpResponseMessage Ok(params string[] features) =>
        StubHttpMessageHandler.Json(HttpStatusCode.OK, $$"""{"features":[{{string.Join(",", features)}}]}""");

    /// <summary>
    /// One live, well-formed alert, with the fields a test is varying pulled
    /// out as parameters - so each test reads as the one thing it changes.
    /// </summary>
    private static string Feature(
        string id,
        string severity = "Severe",
        string status = "Actual",
        string messageType = "Alert",
        string? onset = "2026-08-21T15:00:00+00:00",
        string? ends = "2026-08-21T20:00:00+00:00",
        string? expires = null) =>
        $$$"""
        {"properties":{
          "id":"{{{id}}}",
          "event":"Winter Storm Warning",
          "severity":"{{{severity}}}",
          "status":"{{{status}}}",
          "messageType":"{{{messageType}}}",
          "onset":{{{Json(onset)}}},
          "ends":{{{Json(ends)}}},
          "expires":{{{Json(expires)}}}
        }}
        """;

    private static string Json(string? value) => value is null ? "null" : $"\"{value}\"";

    private static (NwsAlertProvider Provider, StubHttpMessageHandler Handler) NewProvider(
        HttpResponseMessage response, string? contact = null)
    {
        var handler = new StubHttpMessageHandler(response);
        return (new NwsAlertProvider(
            new StubHttpClientFactory(handler),
            new StubSiteSettings(weatherAlertContact: contact),
            new FakeTimeProvider(Now),
            NullLogger<NwsAlertProvider>.Instance), handler);
    }
}
