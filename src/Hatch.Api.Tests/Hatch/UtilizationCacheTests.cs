using Hatch.Api.Modules.Hatch;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// The two floors and the degraded answers.
///
/// Everything here is about how often somebody else's unpublished endpoint gets
/// asked, and what Hatch says while it cannot be asked at all: twenty open tabs
/// are one read, an outage is not a request per tab per poll, and a battery
/// that has lost contact says how old its number is rather than going blank or
/// - worse - going quietly wrong.
/// </summary>
public class UtilizationCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadsUpstreamOnce_ForTwoReadsInsideTheFreshnessWindow()
    {
        var (cache, client, time) = NewCache(Usage(17));

        var first = await cache.ReadAsync(refresh: false, CancellationToken.None);
        time.Advance(UtilizationCache.FreshFor - TimeSpan.FromSeconds(1));
        var second = await cache.ReadAsync(refresh: false, CancellationToken.None);

        Assert.Equal(1, client.Calls);
        Assert.Equal(UtilizationStates.Ok, second!.State);
        // Same reading, and it still says when it was taken rather than now.
        Assert.Equal(first!.ReadAt, second.ReadAt);
        Assert.Equal(Start, second.ReadAt);
    }

    [Fact]
    public async Task ReadsUpstreamAgain_OnceTheWindowHasPassed()
    {
        var (cache, client, time) = NewCache(Usage(17), Usage(40));

        await cache.ReadAsync(refresh: false, CancellationToken.None);
        time.Advance(UtilizationCache.FreshFor + TimeSpan.FromSeconds(1));
        var second = await cache.ReadAsync(refresh: false, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Equal(40, second!.Limits[0].Percent);
    }

    [Fact]
    public async Task RefreshBypassesTheFreshnessWindow()
    {
        var (cache, client, _) = NewCache(Usage(17), Usage(40));

        await cache.ReadAsync(refresh: false, CancellationToken.None);
        var refreshed = await cache.ReadAsync(refresh: true, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Equal(40, refreshed!.Limits[0].Percent);
    }

    /// <summary>Twenty tabs waking together are one read: the gate serialises them and the re-check inside it means nineteen find the answer already there.</summary>
    [Fact]
    public async Task CollapsesSimultaneousReadsIntoOneUpstreamCall()
    {
        var (cache, client, _) = NewCache(Usage(17));

        var reads = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.ReadAsync(refresh: false, CancellationToken.None)));

        Assert.Equal(1, client.Calls);
        Assert.All(reads, reading => Assert.Equal(UtilizationStates.Ok, reading!.State));
    }

    [Fact]
    public async Task StaleWithTheLastGoodReading_WhenTheAccountCannotBeReached()
    {
        var (cache, _, time) = NewCache(Usage(17), Unreachable());

        await cache.ReadAsync(refresh: false, CancellationToken.None);
        time.Advance(UtilizationCache.FreshFor + TimeSpan.FromSeconds(1));
        var degraded = await cache.ReadAsync(refresh: false, CancellationToken.None);

        Assert.Equal(UtilizationStates.Stale, degraded!.State);
        // The last good reading, and the instant it was taken - which is the
        // whole of what "read 5 minutes ago" is drawn from.
        Assert.Equal(17, degraded.Limits[0].Percent);
        Assert.Equal(Start, degraded.ReadAt);
    }

    [Fact]
    public async Task UnknownWhenThereHasNeverBeenAGoodReading()
    {
        var (cache, _, _) = NewCache(Unreachable());

        var reading = await cache.ReadAsync(refresh: false, CancellationToken.None);

        Assert.Equal(UtilizationStates.Unknown, reading!.State);
        Assert.Null(reading.ReadAt);
        Assert.Empty(reading.Limits);
        Assert.Null(reading.Credits);
    }

    /// <summary>
    /// The failure floor. Without it the one path with no rate limit on it is
    /// the failure path, and an outage becomes a request per open tab per poll
    /// against an endpoint nobody publishes.
    /// </summary>
    [Fact]
    public async Task MakesNoUpstreamCall_ForASecondFailureInsideTheBackoff()
    {
        var (cache, client, time) = NewCache(Unreachable());

        await cache.ReadAsync(refresh: false, CancellationToken.None);
        time.Advance(UtilizationCache.RetryAfterFailure - TimeSpan.FromSeconds(1));
        var second = await cache.ReadAsync(refresh: false, CancellationToken.None);

        Assert.Equal(1, client.Calls);
        Assert.Equal(UtilizationStates.Unknown, second!.State);
    }

    [Fact]
    public async Task TriesAgain_OnceTheBackoffHasPassed()
    {
        var (cache, client, time) = NewCache(Unreachable(), Usage(17));

        await cache.ReadAsync(refresh: false, CancellationToken.None);
        time.Advance(UtilizationCache.RetryAfterFailure + TimeSpan.FromSeconds(1));
        var second = await cache.ReadAsync(refresh: false, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Equal(UtilizationStates.Ok, second!.State);
    }

    /// <summary>One person pressing a button once is allowed past a floor that exists to bound twenty tabs polling.</summary>
    [Fact]
    public async Task RefreshBypassesTheFailureBackoff()
    {
        var (cache, client, _) = NewCache(Unreachable(), Usage(17));

        await cache.ReadAsync(refresh: false, CancellationToken.None);
        var refreshed = await cache.ReadAsync(refresh: true, CancellationToken.None);

        Assert.Equal(2, client.Calls);
        Assert.Equal(UtilizationStates.Ok, refreshed!.State);
    }

    /// <summary>No token is null all the way out, which the endpoint turns into a 204 - not an error, and not a state with a backoff.</summary>
    [Fact]
    public async Task NullWhenNoTokenIsConfigured()
    {
        var (cache, _, _) = NewCache(NotConfigured());

        Assert.Null(await cache.ReadAsync(refresh: false, CancellationToken.None));
    }

    /// <summary>A token taken away takes the battery with it, rather than leaving one running on a reading nobody can refresh.</summary>
    [Fact]
    public async Task ForgetsTheLastGoodReading_WhenTheTokenIsRemoved()
    {
        var (cache, _, time) = NewCache(Usage(17), NotConfigured(), Unreachable());

        await cache.ReadAsync(refresh: false, CancellationToken.None);
        time.Advance(UtilizationCache.FreshFor + TimeSpan.FromSeconds(1));
        Assert.Null(await cache.ReadAsync(refresh: false, CancellationToken.None));

        time.Advance(UtilizationCache.RetryAfterFailure + TimeSpan.FromSeconds(1));
        var afterwards = await cache.ReadAsync(refresh: false, CancellationToken.None);

        Assert.Equal(UtilizationStates.Unknown, afterwards!.State);
    }

    private static ClaudeUsageResult Usage(int percent) => ClaudeUsageResult.Ok(new ClaudeUsage(
        [new UtilizationLimit(UtilizationWindows.Session, "Session", percent, UtilizationTones.Normal, null, true)],
        Credits: null));

    private static ClaudeUsageResult Unreachable() => ClaudeUsageResult.Failed(ClaudeUsageClient.Unreachable);

    private static ClaudeUsageResult NotConfigured() => ClaudeUsageResult.Failed(ClaudeUsageClient.NotConfigured);

    private static (UtilizationCache Cache, CountingUsageClient Client, FakeTimeProvider Time) NewCache(
        params ClaudeUsageResult[] answers)
    {
        var time = new FakeTimeProvider(Start);
        var client = new CountingUsageClient(answers);
        return (new UtilizationCache(client, time), client, time);
    }
}

/// <summary>Answers from a queue and counts how often it was asked - the count being the point of most of the tests above.</summary>
internal sealed class CountingUsageClient(params ClaudeUsageResult[] answers) : IClaudeUsageClient
{
    public int Calls { get; private set; }

    public Task<ClaudeUsageResult> GetUsageAsync(CancellationToken ct)
    {
        var answer = answers[Math.Min(Calls, answers.Length - 1)];
        Calls++;
        return Task.FromResult(answer);
    }
}
