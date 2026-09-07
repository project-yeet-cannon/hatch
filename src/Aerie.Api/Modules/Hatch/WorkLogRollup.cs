using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// What a subtree has cost: the work log, added up through the hierarchy.
///
/// Its own file rather than a method on <see cref="Rollup"/>, and the reason is
/// that the rule is the opposite one. A status rollup counts <em>leaves</em>,
/// because a parent sitting in review whose tasks are all in todo has not done
/// the tasks' work and counting its own column would be counting it twice.
/// Spend is not like that: a session run against an epic is money no child
/// holds, so the total is plain addition - this issue's own entries plus every
/// descendant's, at any depth.
///
/// Two rules that look alike and are not is exactly the kind of thing that gets
/// quietly unified by a later refactor, so they sit in two files and each says
/// why.
///
/// The same rows fold a second way here - along a time axis instead of the
/// hierarchy, for the leaderboard's graph - and the two folds share their row
/// projection and their arithmetic on purpose: a bucket's headline figure and an
/// epic's headline figure are one piece of code and cannot come to mean
/// different things.
/// </summary>
public static class WorkLogRollup
{
    /// <summary>
    /// One issue's totals, with or without everything beneath it.
    /// </summary>
    /// <remarks>
    /// "Below this issue" is <see cref="Rollup.DescendantIdsAsync"/>, called and
    /// not redefined: two definitions of descendant is the divergence nobody
    /// notices until a filter and a meter disagree about the same epic.
    ///
    /// One aggregate query over <c>IssueId IN (self ∪ descendants)</c>, so a
    /// deep epic costs one round trip rather than one per story. An issue with
    /// nothing beneath it and nothing on it totals zero rather than erroring -
    /// which is most issues, most of the time.
    /// </remarks>
    public static async Task<WorkLogTotalsDto> TotalsAsync(
        HatchContext db, long issueId, bool includeDescendants, CancellationToken ct)
    {
        var ids = await ScopeAsync(db, issueId, includeDescendants, ct);

        return Fold(await Project(db.WorkLog.Where(w => ids.Contains(w.IssueId))).ToListAsync(ct));
    }

    /// <summary>
    /// Spend over time: an unbroken oldest-first run of equal buckets spanning
    /// an aligned window, optionally narrowed to one issue and everything
    /// beneath it.
    /// </summary>
    /// <remarks>
    /// The window comes in already aligned, from <see cref="Align"/>, because the
    /// endpoint has to know how many buckets it is asking for before it agrees to
    /// scan anything.
    ///
    /// A session belongs to the bucket its <c>EndedAt</c> falls in - the instant
    /// the spend was known, and already the column the issue page orders by - so
    /// it is counted exactly once and the buckets sum to
    /// <see cref="WorkLogHistoryDto.Totals"/> by construction rather than by a
    /// check. An empty bucket is a zero and never an omission: an hour in which
    /// nothing ran cost nothing, which is a measurement.
    ///
    /// One query for the fold, plus two single-row reads for the bounds - which
    /// deliberately ignore the range, so a page can tell "nothing has ever run"
    /// from "nothing ran in the range you asked for".
    /// </remarks>
    public static async Task<WorkLogHistoryDto> SeriesAsync(
        HatchContext db, WorkLogWindow window, WorkLogBucketSize bucket, long? ancestorId, CancellationToken ct)
    {
        var to = window.From + window.Length * window.Buckets;

        // No IssueId filter at all when nothing was named: the whole log is the
        // ordinary case here, unlike TotalsAsync, which is always about one
        // issue.
        var query = db.WorkLog.AsQueryable();
        if (ancestorId is { } id)
        {
            var ids = await ScopeAsync(db, id, includeDescendants: true, ct);
            query = query.Where(w => ids.Contains(w.IssueId));
        }

        var rows = await Project(query.Where(w => w.EndedAt >= window.From && w.EndedAt < to)).ToListAsync(ct);

        var byBucket = rows
            // The range filter above is what guarantees this index lands inside.
            .GroupBy(r => (int)((r.EndedAt - window.From).Ticks / window.Length.Ticks))
            .ToDictionary(g => g.Key, g => g.ToList());

        var buckets = new List<WorkLogBucketDto>((int)window.Buckets);
        for (var i = 0; i < window.Buckets; i++)
        {
            var start = window.From + window.Length * i;
            buckets.Add(new WorkLogBucketDto(
                start,
                start + window.Length,
                Fold(byBucket.GetValueOrDefault(i) ?? NoRows)));
        }

        return new WorkLogHistoryDto(
            window.From,
            to,
            Name(bucket),
            Fold(rows),
            await FirstEndedAsync(db, ancestorId, ascending: true, ct),
            await FirstEndedAsync(db, ancestorId, ascending: false, ct),
            buckets);
    }

    /// <summary>
    /// The requested range, floored onto the bucket grid and counted - without
    /// touching the work log, because the count is what decides whether the log
    /// is scanned at all.
    /// </summary>
    /// <remarks>
    /// The grid is <c>k * Length - shift</c>, where the shift is
    /// <paramref name="offsetMinutes"/> for a daily bucket and zero for an
    /// hourly one. Hourly ignores the offset deliberately: every whole-hour zone
    /// lands on the same grid anyway, and a half-hour zone gets a label half an
    /// hour off rather than a wrong answer about which hour a session ran in.
    ///
    /// A day is 24 hours exactly. <paramref name="offsetMinutes"/> is a fixed
    /// offset and not a zone, so there is no DST seam to reason about - the same
    /// simplification <c>WorkController.DayNumber</c> makes, for the same
    /// reason.
    ///
    /// <c>from == to</c> is one bucket rather than none, which is what keeps an
    /// empty range from answering with nothing at all.
    /// </remarks>
    public static WorkLogWindow Align(
        DateTimeOffset from, DateTimeOffset to, WorkLogBucketSize bucket, int offsetMinutes)
    {
        var length = bucket == WorkLogBucketSize.Day ? TimeSpan.FromHours(24) : TimeSpan.FromHours(1);
        var shift = bucket == WorkLogBucketSize.Day ? offsetMinutes * TimeSpan.TicksPerMinute : 0L;

        // Floored rather than divided: C# truncates toward zero, which would put
        // an instant before the epoch in the bucket above the one it belongs to.
        var start = FloorDiv(from.UtcTicks + shift, length.Ticks) * length.Ticks - shift;

        // How many whole buckets reach `to`, rounded up - so the far end is the
        // grid boundary at or after it, and the requested range is snapped
        // outward on both sides. Computed in ticks, so a decade of hourly
        // buckets is a large number rather than an overflow.
        var buckets = Math.Max(1, FloorDiv(to.UtcTicks - start + length.Ticks - 1, length.Ticks));

        return new WorkLogWindow(new DateTimeOffset(start, TimeSpan.Zero), buckets, length);
    }

    /// <summary>The wire form of a bucket size, which is its lowercase name.</summary>
    public static string Name(WorkLogBucketSize bucket) => bucket == WorkLogBucketSize.Day ? "day" : "hour";

    // ---- The pieces both folds are made of ----

    /// <summary>
    /// The issue ids a total covers: itself, and everything beneath it when
    /// asked.
    /// </summary>
    private static async Task<List<long>> ScopeAsync(
        HatchContext db, long issueId, bool includeDescendants, CancellationToken ct)
    {
        var ids = new List<long> { issueId };
        if (includeDescendants) ids.AddRange(await Rollup.DescendantIdsAsync(db, issueId, ct));
        return ids;
    }

    /// <summary>
    /// One session, as either fold reads it. <c>EndedAt</c> is here for the
    /// series and is ignored by <see cref="TotalsAsync"/>.
    /// </summary>
    private readonly record struct Row(
        DateTimeOffset EndedAt,
        bool IsError,
        long InputTokens,
        long OutputTokens,
        long CacheCreationTokens,
        long CacheReadTokens,
        decimal CostUsd);

    /// <summary>Shared, because an empty bucket is the common case and each one would otherwise allocate a list to say so.</summary>
    private static readonly List<Row> NoRows = [];

    private static IQueryable<Row> Project(IQueryable<EfHatchWorkLogEntry> entries) =>
        entries.AsNoTracking()
            .Select(w => new Row(
                w.EndedAt,
                w.IsError,
                w.InputTokens,
                w.OutputTokens,
                w.CacheCreationTokens,
                w.CacheReadTokens,
                w.CostUsd));

    /// <summary>
    /// The arithmetic, in one place so a bucket's headline and a subtree's
    /// headline cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Summed in memory rather than by the database, which is the one place this
    /// differs from what a production-sized table would want and is the same
    /// trade every other fold in the module makes: EF's in-memory provider does
    /// not implement Sum over a decimal projection the way Npgsql does, and a
    /// work log is one row per increment.
    /// </remarks>
    private static WorkLogTotalsDto Fold(IReadOnlyCollection<Row> rows)
    {
        var input = rows.Sum(r => r.InputTokens);
        var output = rows.Sum(r => r.OutputTokens);
        var cacheCreation = rows.Sum(r => r.CacheCreationTokens);
        var cacheRead = rows.Sum(r => r.CacheReadTokens);

        return new WorkLogTotalsDto(
            rows.Count,
            rows.Count(r => r.IsError),
            input,
            output,
            cacheCreation,
            cacheRead,
            // The headline, added up here so it has one definition - the same
            // reason WorkLogEntryDto carries its own rather than leaving it to
            // the client.
            input + output + cacheCreation + cacheRead,
            // An errored session's spend counts. It ran, and it was billed for
            // running.
            rows.Sum(r => r.CostUsd));
    }

    /// <summary>
    /// The earliest or latest session in the filtered log, ignoring the range -
    /// or null when that population is empty.
    /// </summary>
    /// <remarks>
    /// The scope is applied and the range is not, deliberately: a graph narrowed
    /// to an epic wants the epic's span rather than the house's, and a page needs
    /// the span to choose a range at all.
    /// </remarks>
    private static async Task<DateTimeOffset?> FirstEndedAsync(
        HatchContext db, long? ancestorId, bool ascending, CancellationToken ct)
    {
        var query = db.WorkLog.AsNoTracking();
        if (ancestorId is { } id)
        {
            var ids = await ScopeAsync(db, id, includeDescendants: true, ct);
            query = query.Where(w => ids.Contains(w.IssueId));
        }

        var ordered = ascending ? query.OrderBy(w => w.EndedAt) : query.OrderByDescending(w => w.EndedAt);

        // Projected to a nullable rather than read as a row, so an empty log is
        // null rather than a default instant - the form the module already uses
        // for "the id, or nothing".
        return await ordered.Select(w => (DateTimeOffset?)w.EndedAt).FirstOrDefaultAsync(ct);
    }

    /// <summary>Integer division that floors, which <c>/</c> does not for a negative numerator.</summary>
    private static long FloorDiv(long value, long divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>How long one bucket of the work log's time axis is.</summary>
public enum WorkLogBucketSize
{
    /// <summary>One hour, on the hour in UTC.</summary>
    Hour,

    /// <summary>Twenty-four hours, from midnight at the caller's offset.</summary>
    Day,
}

/// <summary>
/// A range snapped onto the bucket grid: where it starts, how many buckets it
/// is, and how long one of them lasts.
/// </summary>
/// <remarks>
/// The far end is deliberately not a field. It is
/// <c>From + Length * Buckets</c>, computed by <see cref="WorkLogRollup.SeriesAsync"/>
/// - which is only ever reached once the caller has refused an unreasonable
/// count, and that ordering is what keeps the multiplication in range.
/// </remarks>
public readonly record struct WorkLogWindow(DateTimeOffset From, long Buckets, TimeSpan Length);
