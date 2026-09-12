namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// A reading as the endpoint answers it: how good it is, when it was taken,
/// and what it said.
///
/// <paramref name="State"/> is the whole of the degraded story, so a client
/// reads one field rather than inferring staleness from a timestamp:
/// <see cref="UtilizationStates.Ok"/> read within the freshness window;
/// <see cref="UtilizationStates.Stale"/> the account could not be reached and
/// this is the last good reading, whose age <paramref name="ReadAt"/> gives;
/// <see cref="UtilizationStates.Unknown"/> could not be reached and there has
/// never been a good reading, so the rows are empty and there is no instant to
/// report.
/// </summary>
public record UtilizationReading(
    string State,
    DateTimeOffset? ReadAt,
    IReadOnlyList<UtilizationLimit> Limits,
    UtilizationCredits? Credits);

public static class UtilizationStates
{
    public const string Ok = "ok";
    public const string Stale = "stale";
    public const string Unknown = "unknown";
}

/// <summary>
/// The one reading the whole house shares, and the two floors that keep an
/// open tab per room from becoming a request per tab.
///
/// A singleton holding the last good reading and the instant it was taken,
/// <em>forever</em> - not an <c>IMemoryCache</c> entry. "The last good reading,
/// however old" is exactly what an eviction policy would throw away, and it is
/// the whole of what a <see cref="UtilizationStates.Stale"/> answer is made of.
///
/// Two floors, for two different problems:
///
/// - <see cref="FreshFor"/> - a reading younger than this is served without
///   asking. Twenty open tabs polling every two minutes are one upstream read
///   every five, and the semaphore below (with its re-check inside) collapses
///   even a simultaneous twenty into one.
/// - <see cref="RetryAfterFailure"/> - a failed attempt is remembered, so an
///   outage does not turn every tab's poll into a request against an
///   unpublished endpoint. Without it the failure path is the <em>only</em>
///   path with no rate limit on it, which is precisely backwards.
///
/// A refresh the operator asked for ignores both. It is one click by one
/// person, and the point of the button is to not be told what the server
/// already decided.
/// </summary>
public class UtilizationCache(IClaudeUsageClient client, TimeProvider time)
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim gate = new(1, 1);

    private ClaudeUsage? lastGood;
    private DateTimeOffset? lastGoodAt;
    private DateTimeOffset? lastFailureAt;

    /// <summary>
    /// The current reading, or null when no token is configured - which is the
    /// answer the endpoint turns into a 204 and the client reads as "there is
    /// no battery on this installation".
    /// </summary>
    public async Task<UtilizationReading?> ReadAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && Fresh(out var served)) return served;

        await gate.WaitAsync(ct);
        try
        {
            // Re-checked inside the gate: the twenty tabs that arrived together
            // queued here, and nineteen of them are now asking about a reading
            // the first one already took.
            if (!refresh && Fresh(out var justTaken)) return justTaken;

            // The same re-check for the failure floor, and for the same reason.
            if (!refresh && lastFailureAt is { } failedAt && time.GetUtcNow() - failedAt < RetryAfterFailure)
                return Degraded();

            var result = await client.GetUsageAsync(ct);

            if (result.Error == ClaudeUsageClient.NotConfigured)
            {
                // Not remembered as a failure: nothing was attempted, so there
                // is nothing to back off from. The token being removed also
                // drops what was read under it rather than leaving a battery
                // running on a reading nobody can refresh.
                lastGood = null;
                lastGoodAt = null;
                lastFailureAt = null;
                return null;
            }

            if (!result.Succeeded || result.Value is null)
            {
                lastFailureAt = time.GetUtcNow();
                return Degraded();
            }

            lastGood = result.Value;
            lastGoodAt = time.GetUtcNow();
            lastFailureAt = null;
            return new UtilizationReading(UtilizationStates.Ok, lastGoodAt, lastGood.Limits, lastGood.Credits);
        }
        finally
        {
            gate.Release();
        }
    }

    private bool Fresh(out UtilizationReading? reading)
    {
        if (lastGood is { } usage && lastGoodAt is { } at && time.GetUtcNow() - at < FreshFor)
        {
            reading = new UtilizationReading(UtilizationStates.Ok, at, usage.Limits, usage.Credits);
            return true;
        }

        reading = null;
        return false;
    }

    /// <summary>The last good reading with its age, or - when there has never been one - an answer that says so and carries no rows.</summary>
    private UtilizationReading Degraded() => lastGood is { } usage && lastGoodAt is { } at
        ? new UtilizationReading(UtilizationStates.Stale, at, usage.Limits, usage.Credits)
        : new UtilizationReading(UtilizationStates.Unknown, null, [], null);
}
