using System.Net;
using Aerie.Api.Modules.Hatch;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// Covers the wire and the six traps in the payload: that all three headers
/// travel, that the recorded response parses to rows in the account's own
/// order, that a null <c>resets_at</c> and an unfamiliar <c>kind</c> are both
/// ordinary rather than fatal, that a model-scoped row is named by the
/// account's word for it, and that every failure comes back as a value rather
/// than as an exception.
///
/// The payload below is the shape re-probed live during planning, with the
/// values neutralised. The model name in it appears nowhere else in Aerie on
/// purpose: it is the proof that the label is carried through from the account
/// rather than recognised from a list here.
/// </summary>
public class ClaudeUsageClientTests
{
    private const string Token = "sk-ant-oat-test-token";

    private const string ScopedModelName = "Some Model Nobody Here Names";

    private const string RecordedPayload = $$"""
    {
      "five_hour": { "utilization": 17.0, "resets_at": "2026-09-07T12:00:00Z", "limit_dollars": null },
      "seven_day": { "utilization": 16.0, "resets_at": "2026-09-11T12:00:00Z" },
      "seven_day_opus": null,
      "limits": [
        { "kind": "session", "group": "session", "percent": 17, "severity": "normal",
          "resets_at": "2026-09-07T12:00:00Z", "scope": null, "is_active": true },
        { "kind": "weekly_all", "group": "weekly", "percent": 16, "severity": "normal",
          "resets_at": "2026-09-11T12:00:00Z", "scope": null, "is_active": false },
        { "kind": "weekly_scoped", "group": "weekly", "percent": 0, "severity": "normal",
          "resets_at": null,
          "scope": { "model": { "id": null, "display_name": "{{ScopedModelName}}" }, "surface": null },
          "is_active": false }
      ],
      "extra_usage": { "is_enabled": true, "monthly_limit": null, "used_credits": 0.0,
                       "utilization": null, "currency": "USD", "decimal_places": 2,
                       "spend_limit_reached": false },
      "spend": { "used": { "amount_minor": 0, "currency": "USD", "exponent": 2 }, "percent": 0 },
      "member_dashboard_available": false
    }
    """;

    [Fact]
    public async Task NotConfigured_WhenThereIsNoToken_AndNothingIsCalled()
    {
        var (client, handler) = NewClient(token: null, responses: Ok(RecordedPayload));

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(ClaudeUsageClient.NotConfigured, result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendsTheUrlAndTheThreeHeaders()
    {
        var (client, handler) = NewClient(responses: Ok(RecordedPayload));

        await client.GetUsageAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(ClaudeUsageClient.UsageUrl, request.Url);
        Assert.Equal($"Bearer {Token}", request.Headers["Authorization"]);
        Assert.Equal(ClaudeUsageClient.BetaValue, request.Headers[ClaudeUsageClient.BetaHeader]);
        Assert.Equal(ClaudeUsageClient.VersionValue, request.Headers[ClaudeUsageClient.VersionHeader]);
    }

    [Fact]
    public async Task ReadsTheRecordedPayloadAsThreeRowsInOrder()
    {
        var (client, _) = NewClient(responses: Ok(RecordedPayload));

        var usage = (await client.GetUsageAsync(CancellationToken.None)).Value!;

        Assert.Collection(usage.Limits,
            row =>
            {
                Assert.Equal(UtilizationWindows.Session, row.Window);
                Assert.Equal("Session", row.Label);
                Assert.Equal(17, row.Percent);
                Assert.Equal(UtilizationTones.Normal, row.Tone);
                Assert.Equal(DateTimeOffset.Parse("2026-09-07T12:00:00Z"), row.ResetsAt);
                Assert.True(row.IsActive);
            },
            row =>
            {
                Assert.Equal(UtilizationWindows.Weekly, row.Window);
                Assert.Equal("Weekly", row.Label);
                Assert.Equal(16, row.Percent);
                Assert.False(row.IsActive);
            },
            row =>
            {
                Assert.Equal(UtilizationWindows.WeeklyModel, row.Window);
                Assert.Equal(ScopedModelName, row.Label);
                // The trap this whole file exists for: the scoped row arrives
                // with no reset instant at all.
                Assert.Null(row.ResetsAt);
            });
    }

    [Fact]
    public async Task ReadsExtraUsageAsCredits_AndIgnoresSpend()
    {
        var (client, _) = NewClient(responses: Ok(RecordedPayload));

        var credits = (await client.GetUsageAsync(CancellationToken.None)).Value!.Credits!;

        Assert.True(credits.IsEnabled);
        Assert.Null(credits.MonthlyLimit);
        Assert.Equal(0m, credits.UsedCredits);
        Assert.Equal("USD", credits.Currency);
        Assert.False(credits.SpendLimitReached);
    }

    /// <summary>An account with no extra usage at all leaves the modal saying nothing about credits, which is why this is null rather than a zeroed block.</summary>
    [Fact]
    public async Task CreditsAreNull_WhenTheAccountReportsNoExtraUsage()
    {
        var (client, _) = NewClient(responses: Ok("""{ "limits": [] }"""));

        Assert.Null((await client.GetUsageAsync(CancellationToken.None)).Value!.Credits);
    }

    /// <summary>
    /// The day a fourth kind appears, it draws a row with a plain name rather
    /// than a blank one - and it is not dropped, because a limit Hatch cannot
    /// classify is still a limit the account is enforcing.
    /// </summary>
    [Fact]
    public async Task CarriesAnUnknownKindThroughAsOther_WithAReadableLabel()
    {
        var (client, _) = NewClient(responses: Ok("""
        { "limits": [ { "kind": "monthly_something", "group": "monthly", "percent": 4,
                        "severity": "normal", "resets_at": null, "is_active": true } ] }
        """));

        var row = Assert.Single((await client.GetUsageAsync(CancellationToken.None)).Value!.Limits);

        Assert.Equal(UtilizationWindows.Other, row.Window);
        Assert.Equal("Monthly something", row.Label);
    }

    /// <summary>A row with no kind and no group at all still has to be nameable; a blank row in a list is worse than a generic one.</summary>
    [Fact]
    public async Task NamesARowWithNeitherKindNorGroup()
    {
        var (client, _) = NewClient(responses: Ok("""{ "limits": [ { "percent": 3 } ] }"""));

        Assert.Equal("Limit", Assert.Single((await client.GetUsageAsync(CancellationToken.None)).Value!.Limits).Label);
    }

    /// <summary>A scoped row whose model has no display name falls back to its kind rather than to an empty string.</summary>
    [Fact]
    public async Task NamesAScopedRowFromItsKind_WhenTheModelHasNoDisplayName()
    {
        var (client, _) = NewClient(responses: Ok("""
        { "limits": [ { "kind": "weekly_scoped", "group": "weekly", "percent": 0,
                        "scope": { "model": { "id": null, "display_name": null } } } ] }
        """));

        var row = Assert.Single((await client.GetUsageAsync(CancellationToken.None)).Value!.Limits);

        Assert.Equal(UtilizationWindows.WeeklyModel, row.Window);
        Assert.Equal("Weekly scoped", row.Label);
    }

    [Theory]
    [InlineData("normal", 10, UtilizationTones.Normal)]
    [InlineData("warning", 10, UtilizationTones.Warn)]
    [InlineData("critical", 10, UtilizationTones.Danger)]
    public async Task MapsAKnownSeverityToATone(string severity, int percent, string tone)
    {
        var (client, _) = NewClient(responses: Ok($$"""
        { "limits": [ { "kind": "session", "percent": {{percent}}, "severity": "{{severity}}" } ] }
        """));

        Assert.Equal(tone, Assert.Single((await client.GetUsageAsync(CancellationToken.None)).Value!.Limits).Tone);
    }

    /// <summary>
    /// The whole reason tone is decided here rather than passed through:
    /// severity is an open vocabulary, and a word Hatch has never seen must not
    /// be able to paint a spent window in the calm colour.
    /// </summary>
    [Theory]
    [InlineData("brand_new_word", 91, UtilizationTones.Danger)]
    [InlineData("brand_new_word", 80, UtilizationTones.Warn)]
    [InlineData("brand_new_word", 20, UtilizationTones.Normal)]
    [InlineData(null, 95, UtilizationTones.Danger)]
    [InlineData(null, 75, UtilizationTones.Warn)]
    [InlineData(null, 74, UtilizationTones.Normal)]
    public async Task FallsBackToThePercentage_ForAnUnrecognisedOrAbsentSeverity(string? severity, int percent, string tone)
    {
        var severityJson = severity is null ? "null" : $"\"{severity}\"";
        var (client, _) = NewClient(responses: Ok($$"""
        { "limits": [ { "kind": "session", "percent": {{percent}}, "severity": {{severityJson}} } ] }
        """));

        Assert.Equal(tone, Assert.Single((await client.GetUsageAsync(CancellationToken.None)).Value!.Limits).Tone);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Unreachable_WhenTheAccountRefuses(HttpStatusCode status)
    {
        var (client, _) = NewClient(responses: new HttpResponseMessage(status));

        var result = await client.GetUsageAsync(CancellationToken.None);

        Assert.Equal(ClaudeUsageClient.Unreachable, result.Error);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Unreachable_WhenTheAnswerIsNotTheDocumentedShape()
    {
        var (client, _) = NewClient(responses: Ok("<html>a proxy error page</html>"));

        Assert.Equal(ClaudeUsageClient.Unreachable, (await client.GetUsageAsync(CancellationToken.None)).Error);
    }

    /// <summary>A body that is valid JSON and simply "null" is not a reading either.</summary>
    [Fact]
    public async Task Unreachable_WhenTheAnswerIsAJsonNull()
    {
        var (client, _) = NewClient(responses: Ok("null"));

        Assert.Equal(ClaudeUsageClient.Unreachable, (await client.GetUsageAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Unreachable_WhenNothingAnswersAtAll()
    {
        var (client, _) = NewClient(handler: new ThrowingHandler(new HttpRequestException("no route to host")));

        Assert.Equal(ClaudeUsageClient.Unreachable, (await client.GetUsageAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Unreachable_WhenTheRequestTimesOut()
    {
        var (client, _) = NewClient(handler: new ThrowingHandler(new TaskCanceledException("timed out")));

        Assert.Equal(ClaudeUsageClient.Unreachable, (await client.GetUsageAsync(CancellationToken.None)).Error);
    }

    /// <summary>The caller's own cancellation is not the account failing, so it is allowed to propagate rather than being reported as an outage.</summary>
    [Fact]
    public async Task PropagatesTheCallersOwnCancellation()
    {
        var (client, _) = NewClient(handler: new ThrowingHandler(new TaskCanceledException("cancelled")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetUsageAsync(cancelled.Token));
    }

    private static HttpResponseMessage Ok(string body) => StubHttpMessageHandler.Json(HttpStatusCode.OK, body);

    private static (ClaudeUsageClient Client, StubHttpMessageHandler Handler) NewClient(
        string? token = Token, HttpMessageHandler? handler = null, params HttpResponseMessage[] responses)
    {
        var stub = new StubHttpMessageHandler(responses.Length > 0 ? responses : [Ok("{}")]);
        var client = new ClaudeUsageClient(
            new StubHttpClientFactory(handler ?? stub),
            new StubClaudeCredential(token),
            NullLogger<ClaudeUsageClient>.Instance);
        return (client, stub);
    }

    private sealed class ThrowingHandler(Exception thrown) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(thrown);
    }
}

/// <summary>The credential as a value: what the token is, with no setting, no database and no decision about where either lives.</summary>
internal sealed class StubClaudeCredential(string? token) : IClaudeCredential
{
    public Task<string?> GetTokenAsync(CancellationToken ct) =>
        Task.FromResult(string.IsNullOrWhiteSpace(token) ? null : token!.Trim());
}
