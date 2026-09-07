using System.Text.Json;
using Aerie.Api.Modules.Hatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// What crosses the wire.
///
/// Two things are worth a test rather than a reading: that no token answers
/// 204 - the answer a client reads as "there is no battery on this
/// installation", and the reason this is an enhancement to a tracker rather
/// than a dependency of one - and that not one Anthropic field name survives
/// into the body, which is what makes a rename upstream a one-file fix.
/// </summary>
public class UtilizationControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoContent_WhenNoTokenIsConfigured()
    {
        var controller = NewController(ClaudeUsageResult.Failed(ClaudeUsageClient.NotConfigured));

        var result = await controller.Get(refresh: false, CancellationToken.None);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task AnswersTheReadingInHatchsOwnVocabulary()
    {
        var controller = NewController(Recorded());

        var reading = (await controller.Get(refresh: false, CancellationToken.None)).Value!;

        Assert.Equal(UtilizationStates.Ok, reading.State);
        Assert.Equal(Start, reading.ReadAt);
        Assert.Collection(reading.Limits,
            row => Assert.Equal("Session", row.Label),
            row => Assert.Equal("Weekly", row.Label));
        Assert.True(reading.Credits!.IsEnabled);
    }

    /// <summary>
    /// The guard on the whole point of proxying this: the account's spelling
    /// stops at ClaudeUsageClient, so the day a field is renamed upstream there
    /// is one file to fix and no page that has gone blank.
    /// </summary>
    [Fact]
    public async Task CarriesNoAnthropicFieldName()
    {
        var controller = NewController(Recorded());

        var reading = (await controller.Get(refresh: false, CancellationToken.None)).Value!;
        var json = JsonSerializer.Serialize(reading, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        foreach (var theirs in new[]
                 {
                     "resets_at", "is_active", "extra_usage", "used_credits", "monthly_limit",
                     "spend_limit_reached", "severity", "kind", "group", "scope", "display_name",
                     "five_hour", "seven_day", "utilization", "limit_dollars", "amount_minor",
                 })
        {
            Assert.DoesNotContain(theirs, json, StringComparison.OrdinalIgnoreCase);
        }

        // And the shape it does carry, so this test fails loudly if the answer
        // is renamed rather than merely cleaned of somebody else's names.
        foreach (var ours in new[] { "\"state\"", "\"readAt\"", "\"limits\"", "\"window\"", "\"label\"", "\"tone\"", "\"resetsAt\"" })
        {
            Assert.Contains(ours, json);
        }
    }

    /// <summary>`?refresh=true` has to reach the cache as a bypass, or the modal's refresh control is a button that re-renders what was already there.</summary>
    [Fact]
    public async Task RefreshReachesTheCacheAsABypass()
    {
        var client = new CountingUsageClient(Recorded());
        var controller = new UtilizationController(new UtilizationCache(client, new FakeTimeProvider(Start)));

        await controller.Get(refresh: false, CancellationToken.None);
        await controller.Get(refresh: false, CancellationToken.None);
        Assert.Equal(1, client.Calls);

        await controller.Get(refresh: true, CancellationToken.None);
        Assert.Equal(2, client.Calls);
    }

    private static ClaudeUsageResult Recorded() => ClaudeUsageResult.Ok(new ClaudeUsage(
        [
            new UtilizationLimit(UtilizationWindows.Session, "Session", 17, UtilizationTones.Normal, Start.AddHours(4), true),
            new UtilizationLimit(UtilizationWindows.Weekly, "Weekly", 16, UtilizationTones.Normal, Start.AddDays(4), false),
        ],
        new UtilizationCredits(IsEnabled: true, MonthlyLimit: null, UsedCredits: 0m, Currency: "USD", SpendLimitReached: false)));

    private static UtilizationController NewController(ClaudeUsageResult answer) =>
        new(new UtilizationCache(new CountingUsageClient(answer), new FakeTimeProvider(Start)));
}
