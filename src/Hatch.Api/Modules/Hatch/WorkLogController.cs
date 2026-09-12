using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// The work log read across issues rather than about one: what the nights cost,
/// and on what.
///
/// A controller of its own because it is the first read of the log that is not
/// about a single ticket - <see cref="IssueWorkLogController"/> sits at
/// <c>api/hatch/issues/{key}/work-log</c> and answers for one issue. The
/// leaderboard's reads live here together, so they parse a range and an ancestor
/// filter once between them.
/// </summary>
/// <remarks>
/// No <c>ICallerIdentity</c> and no <c>NotAKey</c>: these are reads, and reads in
/// this module are open to an administrator and to a <c>hatch</c> key alike. The
/// third cut the write is given is about who may report a meter reading, which
/// has nothing to say here.
///
/// There is no Claude credential anywhere in this path either. A leaderboard on
/// an installation with no subscription token is the whole leaderboard rather
/// than a reduced one, because the work log is Hatch's own record and owes
/// nothing to an outside API.
/// </remarks>
[ApiController]
[Route("api/hatch/work-log")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class WorkLogController(HatchContext db, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// How many buckets one answer will draw, past which the request is a scan
    /// rather than a graph. Hourly that is 41 days; daily, 2.7 years.
    /// </summary>
    public const int MaxBuckets = 1000;

    /// <summary>What a caller who named no range gets: enough nights to see a shape.</summary>
    private static readonly TimeSpan DefaultRange = TimeSpan.FromDays(14);

    /// <summary>The range up to which an unnamed bucket size is hourly.</summary>
    private static readonly TimeSpan HourlyUpTo = TimeSpan.FromHours(48);

    /// <summary>
    /// The most rows one answer will list, past which the request is an export
    /// rather than a leaderboard. The totals cover the whole filter either way,
    /// so a capped answer still adds up honestly.
    /// </summary>
    public const int MaxSessions = 500;

    /// <summary>What a caller who named no limit gets: more than the table draws, and far less than the cap.</summary>
    private const int DefaultSessions = 100;

    /// <summary>
    /// Spend over time: what the work log recorded across a range, in equal
    /// buckets.
    /// </summary>
    /// <remarks>
    /// Every parameter is optional and every absence has an answer rather than a
    /// refusal. The range defaults to the fourteen days ending now; the bucket
    /// size is chosen from the length of the range and <em>named back in the
    /// answer</em>, so a client labels its axis from what the server did rather
    /// than from what it asked for.
    ///
    /// Two things that look like errors and are not: an installation where
    /// nothing has ever run answers a full run of zeroed buckets, because that
    /// is the ordinary state on the first day; and a range reaching back before
    /// the first logged session answers with what exists.
    ///
    /// The bounds arrive as strings and are parsed with
    /// <see cref="IssueMoment.TryParse"/> rather than model-bound as
    /// <c>DateTimeOffset?</c>, so a value written without an offset is read as
    /// UTC instead of in whatever zone the server happens to sit in - the rule
    /// the module already states for ready and due dates.
    /// </remarks>
    /// <param name="offsetMinutes">
    /// Minutes east of UTC, so local is UTC plus this. It aligns daily buckets to
    /// the reader's midnight; an overnight run split across UTC midnight is two
    /// half-nights nobody worked.
    /// </param>
    /// <param name="ancestorKey">
    /// One issue and everything beneath it, <b>that issue included</b> - which is
    /// deliberately not how the same parameter reads on
    /// <c>GET /api/hatch/issues</c>. A planning session run against an epic is
    /// money no child holds, and a graph that dropped it would disagree with the
    /// meter on the issue page.
    /// </param>
    [HttpGet("history")]
    public async Task<ActionResult<WorkLogHistoryDto>> GetHistory(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? bucket,
        [FromQuery] int offsetMinutes = 0,
        [FromQuery] string? ancestorKey = null,
        CancellationToken ct = default)
    {
        if (offsetMinutes is < -1440 or > 1440)
            return BadRequest($"an offset from UTC is minutes between -1440 and 1440 - not {offsetMinutes}");

        if (Range(from, to, out var start, out var end) is { } badRange) return badRange;

        var trimmed = bucket?.Trim();
        WorkLogBucketSize size;
        if (string.IsNullOrEmpty(trimmed))
        {
            // Decided from the range as requested rather than as snapped, so the
            // choice is a function of what the caller typed.
            size = end - start <= HourlyUpTo ? WorkLogBucketSize.Hour : WorkLogBucketSize.Day;
        }
        else if (string.Equals(trimmed, "hour", StringComparison.OrdinalIgnoreCase))
        {
            size = WorkLogBucketSize.Hour;
        }
        else if (string.Equals(trimmed, "day", StringComparison.OrdinalIgnoreCase))
        {
            size = WorkLogBucketSize.Day;
        }
        else
        {
            return BadRequest($"a bucket is hour or day - not \"{bucket}\"");
        }

        var window = WorkLogRollup.Align(start, end, size, offsetMinutes);

        // After the size is chosen, so an automatically-chosen daily bucket over
        // a decade is refused too - and before anything has touched the work
        // log, which is the reason the check is here rather than inside the fold.
        if (window.Buckets > MaxBuckets)
            return BadRequest(
                $"{window.Buckets} buckets is more than the {MaxBuckets} this answers in " +
                "- ask for daily buckets, or a shorter range");

        var (ancestorId, refusal) = await AncestorAsync(ancestorKey, ct);
        if (refusal is not null) return refusal;

        return await WorkLogRollup.SeriesAsync(db, window, size, ancestorId, ct);
    }

    /// <summary>
    /// The sessions in a range, ranked - and what that range cost in total.
    /// </summary>
    /// <remarks>
    /// The ranking and the table are this one read asked twice with a different
    /// sort and a different cap, rather than two endpoints that could come to
    /// disagree about what a session is.
    ///
    /// The range is used <em>as given</em>. That is the one place this differs
    /// from <see cref="GetHistory"/>, which snaps outward onto the bucket grid
    /// it draws on; there is no grid here, and there is deliberately no
    /// <c>offsetMinutes</c> either - it exists next door to put a daily bucket
    /// on the reader's midnight, and this read has nothing to align.
    ///
    /// <c>totals</c> covers the whole filter and not the page that came back, so
    /// a capped table adds up honestly. A caller tells the two apart by
    /// comparing <c>totals.sessions</c> with the length of <c>sessions</c>.
    /// </remarks>
    /// <param name="ancestorKey">
    /// One issue and everything beneath it, <b>that issue included</b> - read
    /// exactly as <see cref="GetHistory"/> reads it, so the ranking, the table
    /// and the graph can never describe different populations.
    /// </param>
    /// <param name="sort">
    /// <c>tokens</c>, <c>cost</c> or <c>ended</c>, and every one of them
    /// descending. Ties break on <c>EndedAt</c> then id, so two reads of one
    /// filter come back in one order.
    /// </param>
    /// <param name="limit">How many rows to list, 1 to <see cref="MaxSessions"/>.</param>
    [HttpGet("sessions")]
    public async Task<ActionResult<WorkLogSessionsDto>> GetSessions(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? ancestorKey = null,
        [FromQuery] string? sort = null,
        [FromQuery] int limit = DefaultSessions,
        CancellationToken ct = default)
    {
        if (Range(from, to, out var start, out var end) is { } badRange) return badRange;

        var trimmed = sort?.Trim();
        WorkLogSort ranking;
        if (string.IsNullOrEmpty(trimmed) || string.Equals(trimmed, "tokens", StringComparison.OrdinalIgnoreCase))
        {
            // The headline figure, and so the default - AERIE-735 decision 2.
            ranking = WorkLogSort.Tokens;
        }
        else if (string.Equals(trimmed, "cost", StringComparison.OrdinalIgnoreCase))
        {
            ranking = WorkLogSort.Cost;
        }
        else if (string.Equals(trimmed, "ended", StringComparison.OrdinalIgnoreCase))
        {
            ranking = WorkLogSort.Ended;
        }
        else
        {
            return BadRequest($"a sort is tokens, cost or ended - not \"{sort}\"");
        }

        // Interpolated from the constant, so the sentence and the cap cannot
        // drift apart.
        if (limit is < 1 or > MaxSessions)
            return BadRequest($"a limit is between 1 and {MaxSessions} - not {limit}");

        var (ancestorId, refusal) = await AncestorAsync(ancestorKey, ct);
        if (refusal is not null) return refusal;

        return await WorkLogRollup.SessionsAsync(db, start, end, ancestorId, ranking, limit, ct);
    }

    /// <summary>
    /// The range both reads take, or the sentence refusing it.
    /// </summary>
    /// <remarks>
    /// Shared rather than written twice, and that is the point of the two reads
    /// sitting on one controller: two copies of "the last fourteen days" is how
    /// a ranking, a table and a graph come to describe different populations.
    ///
    /// The order matters and is the order <see cref="GetHistory"/> has always
    /// refused in - <c>to</c>, then <c>from</c>, then the inversion - so a
    /// request that is wrong twice still comes back with the sentence it did
    /// before.
    /// </remarks>
    private BadRequestObjectResult? Range(
        string? from, string? to, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = default;
        if (!Bound(to, time.GetUtcNow(), out end)) return BadRequest(NotAnInstant(to));
        if (!Bound(from, end - DefaultRange, out start)) return BadRequest(NotAnInstant(from));

        // Equal is legal - one bucket next door, an empty list here. Inverted is
        // not a range at all.
        return start > end ? BadRequest("a range ends before it begins") : null;
    }

    /// <summary>
    /// One issue and everything beneath it: <c>(null, null)</c> is no filter at
    /// all, and <c>(null, refusal)</c> is a key naming no issue.
    /// </summary>
    /// <remarks>
    /// A typo is refused rather than answered as if it matched nothing: a graph
    /// of zeroes and an empty leaderboard are both worse answers than a sentence
    /// is.
    /// </remarks>
    private async Task<(long? Id, BadRequestObjectResult? Refusal)> AncestorAsync(
        string? ancestorKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ancestorKey)) return (null, null);

        if (!IssueKey.TryParse(ancestorKey, out var projectKey, out var number))
            return (null, BadRequest($"there is no {ancestorKey}"));

        var id = await db.Issues.AsNoTracking().WithKey(projectKey, number)
            .Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);

        return id is null ? (null, BadRequest($"there is no {ancestorKey}")) : (id, null);
    }

    /// <summary>A bound as it was typed, or the default when nothing was.</summary>
    private static bool Bound(string? text, DateTimeOffset fallback, out DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            at = fallback;
            return true;
        }

        var parsed = IssueMoment.TryParse(text, out var moment);
        at = parsed ? moment.At : default;
        return parsed;
    }

    /// <summary>In the house's own words, rather than as a framework 400.</summary>
    private static string NotAnInstant(string? text) =>
        $"a range bound is an instant (2026-09-12T17:00:00Z) - not \"{text}\"";
}
