using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The claim, and the one place its rule lives: how long a lease survives, when
/// it is over, what it looks like on the wire, and the sentence that names its
/// holder.
///
/// A class rather than a static so it can hold the TTL, and a singleton because
/// it is a pure function with a class around it - it reads
/// <see cref="HatchOptions"/> and touches neither the clock nor, except through
/// a context handed to it, the database.
/// </summary>
/// <remarks>
/// <para>Every judgement here takes <c>now</c> rather than reading it, so one
/// scan judges every row on a board against a single instant. A pass in which
/// the clock moved between two rows would be a pass that could fold one card
/// and not its neighbour for no reason anybody could reconstruct.</para>
///
/// <para>The writes are the other half of it, and they are why this class
/// exists rather than four copies of a predicate in a controller. Each one is a
/// single conditional <c>UPDATE</c> carrying the guarantee in its
/// <c>WHERE</c>: a take matches only a row nothing live holds, and a refresh or
/// a release matches only a row still carrying the caller's own token. That is
/// what closes the window between a request's read and its write - a runner
/// whose lease expired mid-increment cannot clear the lease that replaced
/// it.</para>
///
/// <para><see cref="Microsoft.EntityFrameworkCore.RelationalQueryableExtensions"/>'s
/// <c>ExecuteUpdateAsync</c> runs outside the change tracker, so an event
/// written beside one of these is a second statement. They are deliberately not
/// wrapped in a transaction: a crash between the two loses a trail row and no
/// correctness.</para>
/// </remarks>
public sealed class IssueClaims(IOptions<HatchOptions> options)
{
    /// <summary>
    /// The TTL a claim is judged against, in seconds. Guarded here and nowhere
    /// else: a configured zero or a negative would make every claim dead on
    /// arrival, and a misconfigured TTL should cost a fallback rather than a
    /// board nobody may claim.
    /// </summary>
    public int TtlSeconds { get; } =
        options.Value.ClaimTtlSeconds > 0 ? options.Value.ClaimTtlSeconds : new HatchOptions().ClaimTtlSeconds;

    /// <summary>The heartbeat at or after which a claim is still alive.</summary>
    public DateTimeOffset Cutoff(DateTimeOffset now) => now.AddSeconds(-TtlSeconds);

    /// <summary>
    /// Whether something holds this issue right now. Exactly at the cutoff is
    /// alive; one tick past it is not.
    /// </summary>
    public bool IsLive(ClaimSnapshot? claim, DateTimeOffset now) =>
        claim is { Token: not null, HeartbeatAt: { } beat } && beat >= Cutoff(now);

    /// <summary>
    /// The claim as a client draws it, or null where there is none <em>or it
    /// has expired</em>. A dead claim is not handed out to be drawn: nobody
    /// downstream should have to redo this arithmetic, and a card showing a
    /// holder that stopped existing four hours ago is worse than a card showing
    /// nothing.
    /// </summary>
    public IssueClaimDto? Project(ClaimSnapshot? claim, DateTimeOffset now) =>
        IsLive(claim, now)
            ? new IssueClaimDto(
                claim!.ClaimedBy ?? "", claim.Runner ?? "",
                claim.ClaimedAt ?? claim.HeartbeatAt!.Value, claim.HeartbeatAt!.Value,
                claim.Chatter, claim.ChatterAt,
                TtlSeconds)
            : null;

    /// <summary>
    /// The one sentence a live claim prints, wherever it is printed: in the
    /// <c>409</c> refusing a second claim, in the row <c>work/queue</c> folds,
    /// and in the dispatch <c>work/{key}</c> refuses. One method called three
    /// times, so a holder-shaped fold cannot come to read three ways.
    /// </summary>
    public string Sentence(ClaimSnapshot claim, DateTimeOffset now) =>
        $"{claim.ClaimedBy} is working this from {claim.Runner}, last heard from {Ago(claim.HeartbeatAt, now)}";

    /// <summary>
    /// How long ago, at the resolution somebody reading a refusal at a terminal
    /// cares about. Seconds under a minute because the interesting case is a
    /// lease taken moments ago by the runner in the next window; minutes after
    /// that, because past a minute nobody is counting.
    /// </summary>
    private static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } beat) return "never";

        var since = now - beat;
        if (since < TimeSpan.FromSeconds(10)) return "just now";
        if (since < TimeSpan.FromMinutes(1)) return $"{(int)since.TotalSeconds} seconds ago";

        var minutes = (int)since.TotalMinutes;
        return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
    }

    // ---- The writes ----

    /// <summary>
    /// Takes the lease, and answers whether it was taken. One conditional
    /// <c>UPDATE</c>: the <c>WHERE</c> is the exact negation of
    /// <see cref="IsLive"/>, so the guarantee lives on the write and two
    /// requests that both decided against the same pre-claim state cannot both
    /// come away holding it.
    /// </summary>
    /// <remarks>
    /// The chatter is cleared with the take. A new lease does not inherit the
    /// last one's last words.
    /// </remarks>
    public async Task<bool> TryTakeAsync(
        HatchContext db, long issueId, Guid token, string actor, string runner,
        DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = Cutoff(now);

        return await db.Issues
            .Where(i => i.Id == issueId
                && (i.ClaimToken == null || i.ClaimHeartbeatAt == null || i.ClaimHeartbeatAt < cutoff))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ClaimToken, token)
                .SetProperty(i => i.ClaimedBy, actor)
                .SetProperty(i => i.ClaimRunner, runner)
                .SetProperty(i => i.ClaimedAt, now)
                .SetProperty(i => i.ClaimHeartbeatAt, now)
                .SetProperty(i => i.ClaimChatter, (string?)null)
                .SetProperty(i => i.ClaimChatterAt, (DateTimeOffset?)null), ct) == 1;
    }

    /// <summary>
    /// Refreshes a live lease, and answers whether there was one to refresh.
    /// Both halves of the predicate matter: the token fences a runner whose
    /// lease was retaken, and the cutoff ends a lease whose own token is still
    /// on the row - once it is over it is over, and a late heartbeat does not
    /// resurrect it.
    /// </summary>
    /// <param name="chatter">
    /// The line to carry: null leaves whatever is there alone, and an empty
    /// string clears it. Already normalised by the caller - see
    /// <see cref="IssueClaimController"/>.
    /// </param>
    public async Task<bool> TryRefreshAsync(
        HatchContext db, long issueId, Guid token, string? chatter, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = Cutoff(now);
        var live = db.Issues.Where(i => i.Id == issueId && i.ClaimToken == token && i.ClaimHeartbeatAt >= cutoff);

        var written = chatter switch
        {
            null => await live.ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ClaimHeartbeatAt, now), ct),
            "" => await live.ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ClaimHeartbeatAt, now)
                .SetProperty(i => i.ClaimChatter, (string?)null)
                .SetProperty(i => i.ClaimChatterAt, (DateTimeOffset?)null), ct),
            _ => await live.ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ClaimHeartbeatAt, now)
                .SetProperty(i => i.ClaimChatter, chatter)
                .SetProperty(i => i.ClaimChatterAt, (DateTimeOffset?)now), ct),
        };

        return written == 1;
    }

    /// <summary>
    /// Clears the lease this token holds, and answers whether it held one.
    /// Unlike <see cref="TryRefreshAsync"/> there is no cutoff in the
    /// predicate: a matching token may tidy up a lease that expired underneath
    /// it, because the row is still the holder's and nobody else has taken it.
    /// </summary>
    public async Task<bool> TryReleaseAsync(
        HatchContext db, long issueId, Guid token, CancellationToken ct) =>
        await Clear(db.Issues.Where(i => i.Id == issueId && i.ClaimToken == token), ct) == 1;

    /// <summary>
    /// Clears whatever is there, whoever holds it - the operator's clobber, and
    /// the only write here with no token in its predicate. Answers whether
    /// there was anything to clear, so releasing an unclaimed issue can write
    /// no event.
    /// </summary>
    public async Task<bool> ClearAsync(
        HatchContext db, long issueId, CancellationToken ct) =>
        await Clear(db.Issues.Where(i => i.Id == issueId && i.ClaimToken != null), ct) == 1;

    /// <summary>The seven columns back to unclaimed, written once so the two release lanes cannot clear different things.</summary>
    private static Task<int> Clear(IQueryable<EfHatchIssue> rows, CancellationToken ct) =>
        rows.ExecuteUpdateAsync(s => s
            .SetProperty(i => i.ClaimToken, (Guid?)null)
            .SetProperty(i => i.ClaimedBy, (string?)null)
            .SetProperty(i => i.ClaimRunner, (string?)null)
            .SetProperty(i => i.ClaimedAt, (DateTimeOffset?)null)
            .SetProperty(i => i.ClaimHeartbeatAt, (DateTimeOffset?)null)
            .SetProperty(i => i.ClaimChatter, (string?)null)
            .SetProperty(i => i.ClaimChatterAt, (DateTimeOffset?)null), ct);
}

/// <summary>
/// An issue's seven claim columns, lifted off the row so that everything
/// judging a claim - a controller with an entity in hand, a scan with a
/// projection, a board read that never materialised an entity at all - asks the
/// same question of the same shape.
/// </summary>
public sealed record ClaimSnapshot(
    Guid? Token,
    string? ClaimedBy,
    string? Runner,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? HeartbeatAt,
    string? Chatter,
    DateTimeOffset? ChatterAt)
{
    public static ClaimSnapshot Of(EfHatchIssue issue) => new(
        issue.ClaimToken,
        issue.ClaimedBy,
        issue.ClaimRunner,
        issue.ClaimedAt,
        issue.ClaimHeartbeatAt,
        issue.ClaimChatter,
        issue.ClaimChatterAt);
}
