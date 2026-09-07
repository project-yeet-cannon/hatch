using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

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

        if (!Bound(to, time.GetUtcNow(), out var end)) return BadRequest(NotAnInstant(to));
        if (!Bound(from, end - DefaultRange, out var start)) return BadRequest(NotAnInstant(from));

        // Equal is legal and is one bucket. Inverted is not a range at all.
        if (start > end) return BadRequest("a range ends before it begins");

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

        long? ancestorId = null;
        if (!string.IsNullOrWhiteSpace(ancestorKey))
        {
            if (!IssueKey.TryParse(ancestorKey, out var projectKey, out var number))
                return BadRequest($"there is no {ancestorKey}");

            ancestorId = await db.Issues.AsNoTracking().WithKey(projectKey, number)
                .Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);

            // Refused rather than answered as if it matched nothing: a graph of
            // zeroes is a worse answer to a typo than a sentence is.
            if (ancestorId is null) return BadRequest($"there is no {ancestorKey}");
        }

        return await WorkLogRollup.SeriesAsync(db, window, size, ancestorId, ct);
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
