using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// One limit window, in Hatch's own vocabulary rather than the account's.
///
/// <paramref name="ResetsAt"/> is nullable because the upstream row is: a
/// model-scoped weekly window that has not been touched arrives with no reset
/// instant at all. Anything that subtracts two instants has to survive that -
/// a ring with nothing to run down to is drawn as unknown, not as full and not
/// as empty.
/// </summary>
public record UtilizationLimit(
    string Window,
    string Label,
    int Percent,
    string Tone,
    DateTimeOffset? ResetsAt,
    bool IsActive);

/// <summary>
/// Extra usage - credits bought on top of the subscription - as four facts and
/// nothing more.
///
/// The account describes credits twice, in two units, and this reads one of
/// them. The other (<c>spend</c>, in minor units) is deliberately not read:
/// two numbers for the same thing on one screen is a bug report, and picking
/// one here is what stops it.
/// </summary>
public record UtilizationCredits(
    bool IsEnabled,
    decimal? MonthlyLimit,
    decimal UsedCredits,
    string? Currency,
    bool SpendLimitReached);

/// <summary>A whole reading, already reshaped, so the endpoint above it is a serializer rather than a second translation.</summary>
public record ClaudeUsage(IReadOnlyList<UtilizationLimit> Limits, UtilizationCredits? Credits);

/// <summary>
/// A read's outcome. Same shape and same reasoning as
/// <see cref="Photos.ImmichResult{T}"/>: a failure is a value the caller reads,
/// never an exception thrown through it - and here that matters more than
/// usual, because the caller's job is to keep serving the last good reading
/// while the account is unreachable.
/// </summary>
public record ClaudeUsageResult(ClaudeUsage? Value, string? Error)
{
    public bool Succeeded => Error is null;

    public static ClaudeUsageResult Ok(ClaudeUsage value) => new(value, null);

    public static ClaudeUsageResult Failed(string error) => new(null, error);
}

public interface IClaudeUsageClient
{
    /// <summary>The account's current headroom, or why it could not be read. Never throws for anything upstream did.</summary>
    Task<ClaudeUsageResult> GetUsageAsync(CancellationToken ct);
}

/// <summary>The four windows Hatch draws differently. An account may name a fifth, and <see cref="UtilizationWindows.Other"/> is where it lands.</summary>
public static class UtilizationWindows
{
    public const string Session = "session";
    public const string Weekly = "weekly";
    public const string WeeklyModel = "weeklyModel";

    /// <summary>A kind Hatch has never seen. Rendered with a plain name, not dropped and not thrown on - see ClaudeUsageClient's label rule.</summary>
    public const string Other = "other";
}

/// <summary>What a row is painted, decided on the server so the rule lives in one file and the client paints what it is told.</summary>
public static class UtilizationTones
{
    public const string Normal = "normal";
    public const string Warn = "warn";
    public const string Danger = "danger";
}

/// <summary>
/// The Claude subscription account's own utilization endpoint, read by the
/// server and never by a browser.
///
/// Modelled on <see cref="Photos.ImmichClient"/>, the house's other adapter for
/// an API nobody publishes: a named <c>HttpClient</c>, a fail-soft result
/// record rather than exceptions, and constant error strings a caller branches
/// on.
///
/// Everything vendor-shaped stops here. Five properties of the payload are
/// worth naming, because each one decides code below:
///
/// - <c>limits[].resets_at</c> is nullable.
/// - <c>limits[].percent</c> is an integer; the older per-bucket
///   <c>utilization</c> floats are not read at all.
/// - <c>severity</c> is an open vocabulary - only <c>normal</c> has ever been
///   observed - so <see cref="ToneFor"/> may not switch on a guessed list, and
///   may not go blind when something else arrives.
/// - The named per-bucket fields (<c>five_hour</c>, <c>seven_day_opus</c>, and
///   whatever internal codename is beside them this month) are legacy, mostly
///   null, and rotate. <c>limits[]</c> is the shape this reads.
/// - A model-scoped row names its model only by <c>display_name</c>, and its
///   id may be null. That name is carried through as a label and no model name
///   is ever written into Hatch's own source.
/// </summary>
public class ClaudeUsageClient(
    IHttpClientFactory httpClientFactory,
    IClaudeCredential credential,
    ILogger<ClaudeUsageClient> logger) : IClaudeUsageClient
{
    /// <summary>Named for its role rather than its vendor, matching the convention in Program.cs.</summary>
    public const string HttpClientName = "ClaudeUsage";

    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>The subscription usage read is gated behind this beta; without the header the account answers 404.</summary>
    public const string BetaHeader = "anthropic-beta";
    public const string BetaValue = "oauth-2025-04-20";

    public const string VersionHeader = "anthropic-version";
    public const string VersionValue = "2023-06-01";

    /// <summary>No token is configured. Not an error: it is the state an installation with no Claude subscription is permanently in.</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>The account could not be read - refused, down, slow, or answering something that is not the payload. All one state, because a battery cannot act differently on any of them.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>At or above this, a window is spent. Only consulted when the account's own severity is absent or unrecognised - see <see cref="ToneFor"/>.</summary>
    public const int DangerPercent = 90;

    /// <summary>At or above this, a window is worth noticing.</summary>
    public const int WarnPercent = 75;

    public async Task<ClaudeUsageResult> GetUsageAsync(CancellationToken ct)
    {
        // Before the request, not after it: an installation with no
        // subscription must not put traffic on somebody else's endpoint every
        // two minutes to be told what it already knows.
        var token = await credential.GetTokenAsync(ct);
        if (token is null) return ClaudeUsageResult.Failed(NotConfigured);

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation(BetaHeader, BetaValue);
            request.Headers.TryAddWithoutValidation(VersionHeader, VersionValue);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The status is logged and then thrown away: a revoked token
                // and a 500 both mean "no reading", and a battery that showed
                // the difference would be showing an operator a number they
                // cannot act on from the nav strip.
                logger.LogWarning("The Claude account refused the utilization read with {Status}", (int)response.StatusCode);
                return ClaudeUsageResult.Failed(Unreachable);
            }

            var body = JsonSerializer.Deserialize<UsageBody>(await response.Content.ReadAsStringAsync(ct));
            if (body is null) return ClaudeUsageResult.Failed(Unreachable);

            return ClaudeUsageResult.Ok(new ClaudeUsage(Rows(body), CreditsOf(body.ExtraUsage)));
        }
        catch (Exception ex) when (Transient(ex, ct))
        {
            logger.LogWarning(ex, "The Claude account could not be reached for a utilization read");
            return ClaudeUsageResult.Failed(Unreachable);
        }
    }

    /// <summary>A caller's own cancellation is not an upstream failure, so it is allowed to propagate; everything else on the way to the account is one.</summary>
    private static bool Transient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested;

    /// <summary>
    /// The rows, in the order the account listed them. Order is the account's
    /// to decide and nothing here sorts or filters - an inactive window is
    /// still a window, and a row Hatch does not recognise is still a row.
    /// </summary>
    private static List<UtilizationLimit> Rows(UsageBody body) => (body.Limits ?? [])
        .Select(row => new UtilizationLimit(
            WindowFor(row.Kind),
            LabelFor(row),
            // Clamped rather than trusted: the number is drawn as a fill, and
            // a fill is a fraction of something.
            Math.Clamp(row.Percent ?? 0, 0, 100),
            ToneFor(row),
            ParseTime(row.ResetsAt),
            row.IsActive ?? false))
        .ToList();

    /// <summary>
    /// Which of the four shapes a row is. The default is
    /// <see cref="UtilizationWindows.Other"/> rather than a throw, because the
    /// day a fifth kind appears the battery should gain a row, not go blank.
    /// </summary>
    private static string WindowFor(string? kind) => kind switch
    {
        "session" => UtilizationWindows.Session,
        "weekly_all" => UtilizationWindows.Weekly,
        "weekly_scoped" => UtilizationWindows.WeeklyModel,
        _ => UtilizationWindows.Other,
    };

    /// <summary>
    /// What the row is called on screen. One of the two methods a future
    /// change at Anthropic lands in.
    ///
    /// A scoped row is named by the account's own <c>display_name</c>, which is
    /// deliberate: the model names rotate faster than this repository is
    /// edited, so carrying the account's word for it is how the row stays right
    /// without a release - and it is why no model name is written down here.
    ///
    /// Anything unrecognised is named from the account's <c>kind</c> (or its
    /// <c>group</c>) with the underscores turned into spaces, so a row Hatch has
    /// never seen still reads as words rather than as a blank.
    /// </summary>
    private static string LabelFor(LimitBody row)
    {
        if (row.Kind == "session") return "Session";
        if (row.Kind == "weekly_all") return "Weekly";

        if (NullIfEmpty(row.Scope?.Model?.DisplayName) is { } model) return model;

        return Humanized(NullIfEmpty(row.Kind) ?? NullIfEmpty(row.Group)) ?? "Limit";
    }

    /// <summary>"weekly_scoped" reads as "Weekly scoped" - words, sentence-cased, which is enough to put in front of somebody.</summary>
    private static string? Humanized(string? value)
    {
        if (value is null) return null;
        var words = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? null : char.ToUpperInvariant(words[0]) + words[1..];
    }

    /// <summary>
    /// What the row is painted, and the other method a future change lands in.
    ///
    /// The account's severity is an open vocabulary: only <c>normal</c> has ever
    /// been observed, and a switch over a list Hatch guessed at would paint a
    /// spent window in the calm colour the first time a new word arrives. So a
    /// severity Hatch knows maps, and anything else - unrecognised, or absent -
    /// falls back to the number, which is a fact rather than a vocabulary.
    /// </summary>
    private static string ToneFor(LimitBody row) => row.Severity?.ToLowerInvariant() switch
    {
        "normal" or "ok" or "none" => UtilizationTones.Normal,
        "warning" or "warn" or "elevated" => UtilizationTones.Warn,
        "critical" or "danger" or "severe" or "exhausted" => UtilizationTones.Danger,
        _ => TonePercent(row.Percent ?? 0),
    };

    private static string TonePercent(int percent) => percent switch
    {
        >= DangerPercent => UtilizationTones.Danger,
        >= WarnPercent => UtilizationTones.Warn,
        _ => UtilizationTones.Normal,
    };

    /// <summary>
    /// Extra usage, or null when the account reports no block at all - which is
    /// what a modal that says nothing about credits is drawn from.
    /// </summary>
    private static UtilizationCredits? CreditsOf(ExtraUsageBody? body) => body is null
        ? null
        : new UtilizationCredits(
            body.IsEnabled ?? false,
            body.MonthlyLimit,
            body.UsedCredits ?? 0m,
            NullIfEmpty(body.Currency),
            body.SpendLimitReached ?? false);

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// As much of the payload as Hatch reads. The named per-bucket fields
    /// (<c>five_hour</c> and friends) are absent from this record on purpose:
    /// they are legacy, mostly null, and the set of them rotates, so binding
    /// one would be binding a field that disappears.
    /// </summary>
    private record UsageBody(
        [property: JsonPropertyName("limits")] List<LimitBody>? Limits,
        [property: JsonPropertyName("extra_usage")] ExtraUsageBody? ExtraUsage);

    private record LimitBody(
        [property: JsonPropertyName("kind")] string? Kind,
        [property: JsonPropertyName("group")] string? Group,
        [property: JsonPropertyName("percent")] int? Percent,
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("resets_at")] string? ResetsAt,
        [property: JsonPropertyName("scope")] ScopeBody? Scope,
        [property: JsonPropertyName("is_active")] bool? IsActive);

    private record ScopeBody([property: JsonPropertyName("model")] ScopeModelBody? Model);

    /// <summary>The id is nullable and unread - the display name is the whole of what a row is called.</summary>
    private record ScopeModelBody([property: JsonPropertyName("display_name")] string? DisplayName);

    private record ExtraUsageBody(
        [property: JsonPropertyName("is_enabled")] bool? IsEnabled,
        [property: JsonPropertyName("monthly_limit")] decimal? MonthlyLimit,
        [property: JsonPropertyName("used_credits")] decimal? UsedCredits,
        [property: JsonPropertyName("currency")] string? Currency,
        [property: JsonPropertyName("spend_limit_reached")] bool? SpendLimitReached);
}
