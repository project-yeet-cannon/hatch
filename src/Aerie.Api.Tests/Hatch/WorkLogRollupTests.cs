using Aerie.Api.Modules.Hatch;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The addition that is not the leaf rule: a subtree's spend is its own entries
/// plus every descendant's, at any depth, and a parent's total is exactly the
/// sum of its children's.
/// </summary>
public class WorkLogRollupTests
{
    [Fact]
    public async Task AnEpicsTotal_IsItsOwnEntriesPlusEveryDescendants()
    {
        var db = await NewAsync();
        var (epic, story, task) = await ThreeLevelsAsync(db);

        // One session run against each level. The epic's own is the case the
        // leaf rule would lose: planning is work, and nobody's child holds it.
        await SpendAsync(db, epic, tokens: 1_000, usd: 0.10m);
        await SpendAsync(db, story, tokens: 20_000, usd: 2.00m);
        await SpendAsync(db, task, tokens: 300_000, usd: 30.00m);

        var whole = await WorkLogRollup.TotalsAsync(db, epic, includeDescendants: true, default);

        Assert.Equal(3, whole.Sessions);
        Assert.Equal(321_000, whole.TotalTokens);
        Assert.Equal(32.10m, whole.CostUsd);

        // And it is exactly the sum of the level below it, which is what lets a
        // stack of these agree with the one above.
        var storySubtree = await WorkLogRollup.TotalsAsync(db, story, includeDescendants: true, default);
        var epicOwn = await WorkLogRollup.TotalsAsync(db, epic, includeDescendants: false, default);

        Assert.Equal(whole.TotalTokens, epicOwn.TotalTokens + storySubtree.TotalTokens);
        Assert.Equal(whole.CostUsd, epicOwn.CostUsd + storySubtree.CostUsd);
    }

    [Fact]
    public async Task AnEpicWithNoSessionsOfItsOwn_ReportsItsStories()
    {
        var db = await NewAsync();
        var (epic, story, task) = await ThreeLevelsAsync(db);

        await SpendAsync(db, story, tokens: 20_000, usd: 2.00m);
        await SpendAsync(db, task, tokens: 300_000, usd: 30.00m);

        var whole = await WorkLogRollup.TotalsAsync(db, epic, includeDescendants: true, default);
        var own = await WorkLogRollup.TotalsAsync(db, epic, includeDescendants: false, default);

        // The asymmetry the page exists to say out loud: 320k spent beneath it,
        // none of it its own.
        Assert.Equal(320_000, whole.TotalTokens);
        Assert.Equal(2, whole.Sessions);
        Assert.Equal(0, own.TotalTokens);
        Assert.Equal(0, own.Sessions);
    }

    [Fact]
    public async Task AnIssueWithNothingBeneathIt_TotalsZeroRatherThanErroring()
    {
        var db = await NewAsync();
        var alone = await FileAsync(db, "task", null);

        var totals = await WorkLogRollup.TotalsAsync(db, alone, includeDescendants: true, default);

        Assert.Equal(0, totals.Sessions);
        Assert.Equal(0, totals.Errors);
        Assert.Equal(0, totals.TotalTokens);
        Assert.Equal(0m, totals.CostUsd);
    }

    [Fact]
    public async Task AnErroredSession_CountsInTheErrorsAndInTheSpend()
    {
        var db = await NewAsync();
        var (epic, story, _) = await ThreeLevelsAsync(db);

        await SpendAsync(db, story, tokens: 1_000, usd: 0.50m);
        await SpendAsync(db, story, tokens: 4_000, usd: 1.50m, isError: true);

        var totals = await WorkLogRollup.TotalsAsync(db, epic, includeDescendants: true, default);

        // It ran and it was billed for running. Counting the failure separately
        // is what lets a page say so without dropping the money.
        Assert.Equal(2, totals.Sessions);
        Assert.Equal(1, totals.Errors);
        Assert.Equal(5_000, totals.TotalTokens);
        Assert.Equal(2.00m, totals.CostUsd);
    }

    [Fact]
    public async Task TheFourCounts_AreCarriedSeparatelyAndSumToTheHeadline()
    {
        var db = await NewAsync();
        var task = await FileAsync(db, "task", null);

        db.WorkLog.Add(Entry(task, "s1", 1, 2, 4, 8, 0m));
        db.WorkLog.Add(Entry(task, "s2", 16, 32, 64, 128, 0m));
        await db.SaveChangesAsync();

        var totals = await WorkLogRollup.TotalsAsync(db, task, includeDescendants: true, default);

        Assert.Equal(17, totals.InputTokens);
        Assert.Equal(34, totals.OutputTokens);
        Assert.Equal(68, totals.CacheCreationTokens);
        Assert.Equal(136, totals.CacheReadTokens);
        Assert.Equal(255, totals.TotalTokens);
    }

    // ---- The series ----

    [Fact]
    public async Task AnHourlyRange_IsOneBucketPerHourOldestFirstWithTheEmptyOnesZeroed()
    {
        var db = await NewAsync();
        var task = await FileAsync(db, "task", null);

        await SpendAsync(db, task, tokens: 100, usd: 1m, endedAt: At(0, 30));
        await SpendAsync(db, task, tokens: 200, usd: 2m, endedAt: At(2, 15));
        await SpendAsync(db, task, tokens: 300, usd: 3m, endedAt: At(2, 45));
        await SpendAsync(db, task, tokens: 400, usd: 4m, endedAt: At(5, 59));

        var history = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour);

        Assert.Equal(6, history.Buckets.Count);
        Assert.Equal("hour", history.Bucket);

        // Oldest first, an hour apart, each ending where the next begins.
        Assert.Equal(
            Enumerable.Range(0, 6).Select(i => At(0, 0).AddHours(i)),
            history.Buckets.Select(b => b.Start));
        Assert.All(history.Buckets, b => Assert.Equal(TimeSpan.FromHours(1), b.End - b.Start));

        Assert.Equal([1, 0, 2, 0, 0, 1], history.Buckets.Select(b => b.Totals.Sessions));

        // An hour with nothing in it cost nothing, which is a measurement.
        Assert.Equal(0, history.Buckets[1].Totals.TotalTokens);
        Assert.Equal(0m, history.Buckets[1].Totals.CostUsd);
        Assert.Equal(500, history.Buckets[2].Totals.TotalTokens);
        Assert.Equal(5m, history.Buckets[2].Totals.CostUsd);
    }

    [Fact]
    public async Task TheLastInstantOfAnHourAndTheFirstOfTheNext_AreInDifferentBuckets()
    {
        var db = await NewAsync();
        var task = await FileAsync(db, "task", null);

        await SpendAsync(db, task, tokens: 100, usd: 0m, endedAt: At(9, 59).AddSeconds(59).AddMilliseconds(999));
        await SpendAsync(db, task, tokens: 200, usd: 0m, endedAt: At(10, 0));

        var history = await SeriesAsync(db, At(9, 0), At(11, 0), WorkLogBucketSize.Hour);

        // Half-open: start <= EndedAt < end, so a session is counted once and
        // the one on the boundary belongs to the bucket it starts.
        Assert.Equal(2, history.Buckets.Count);
        Assert.Equal(100, history.Buckets[0].Totals.TotalTokens);
        Assert.Equal(200, history.Buckets[1].Totals.TotalTokens);
    }

    [Fact]
    public async Task ADailyBucketAtAnOffset_PutsAnOvernightRunInTheNightItWasWorked()
    {
        var db = await NewAsync();
        var task = await FileAsync(db, "task", null);

        // Two in the morning UTC on the 8th is nine in the evening on the 7th,
        // five hours west - the night somebody actually worked.
        await SpendAsync(db, task, tokens: 100, usd: 0m, endedAt: new DateTimeOffset(2026, 9, 8, 2, 0, 0, TimeSpan.Zero));

        var history = await SeriesAsync(
            db,
            new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 9, 5, 0, 0, TimeSpan.Zero),
            WorkLogBucketSize.Day,
            offsetMinutes: -300);

        Assert.Equal("day", history.Bucket);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 5, 0, 0, TimeSpan.Zero), history.Buckets[0].Start);
        Assert.Equal(1, history.Buckets[0].Totals.Sessions);
        Assert.Equal(0, history.Buckets[1].Totals.Sessions);
    }

    [Fact]
    public async Task HourlyBuckets_IgnoreTheOffsetEntirely()
    {
        var db = await NewAsync();
        var task = await FileAsync(db, "task", null);

        await SpendAsync(db, task, tokens: 100, usd: 0m, endedAt: At(2, 15));

        var utc = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour);
        var west = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour, offsetMinutes: -300);

        // Every whole-hour zone lands on the same grid anyway, and a half-hour
        // zone gets a label half an hour off rather than a wrong answer about
        // which hour a session ran in.
        Assert.Equal(utc.From, west.From);
        Assert.Equal(utc.To, west.To);
        Assert.Equal(
            utc.Buckets.Select(b => (b.Start, b.Totals.Sessions)),
            west.Buckets.Select(b => (b.Start, b.Totals.Sessions)));
    }

    [Fact]
    public async Task AnUnalignedRange_IsSnappedOutwardOntoWholeBuckets()
    {
        var db = await NewAsync();

        var history = await SeriesAsync(db, At(1, 20), At(4, 5), WorkLogBucketSize.Hour);

        // From falls to the bucket below it, To rises to the boundary at or
        // after it - which is what keeps the range, the buckets and the totals
        // describing one window.
        Assert.Equal(At(1, 0), history.From);
        Assert.Equal(At(5, 0), history.To);
        Assert.Equal(4, history.Buckets.Count);
        Assert.Equal(history.From, history.Buckets[0].Start);
        Assert.Equal(history.To, history.Buckets[^1].End);
    }

    [Fact]
    public async Task TheBuckets_AddUpToTheRangesTotals()
    {
        var db = await NewAsync();
        var task = await FileAsync(db, "task", null);

        await SpendAsync(db, task, tokens: 1_000, usd: 0.10m, endedAt: At(0, 30));
        await SpendAsync(db, task, tokens: 20_000, usd: 2.00m, endedAt: At(2, 15));
        await SpendAsync(db, task, tokens: 300_000, usd: 30.00m, endedAt: At(5, 1), isError: true);

        var history = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour);

        Assert.Equal(3, history.Totals.Sessions);
        Assert.Equal(1, history.Totals.Errors);
        Assert.Equal(321_000, history.Totals.TotalTokens);
        Assert.Equal(32.10m, history.Totals.CostUsd);

        // True by construction rather than by a check somebody remembered.
        Assert.Equal(history.Totals.Sessions, history.Buckets.Sum(b => b.Totals.Sessions));
        Assert.Equal(history.Totals.Errors, history.Buckets.Sum(b => b.Totals.Errors));
        Assert.Equal(history.Totals.TotalTokens, history.Buckets.Sum(b => b.Totals.TotalTokens));
        Assert.Equal(history.Totals.CostUsd, history.Buckets.Sum(b => b.Totals.CostUsd));
    }

    [Fact]
    public async Task ASessionOutsideTheRange_IsInNoBucketAndInNoTotal()
    {
        var db = await NewAsync();
        var task = await FileAsync(db, "task", null);

        await SpendAsync(db, task, tokens: 100, usd: 1m, endedAt: At(2, 0));
        await SpendAsync(db, task, tokens: 999, usd: 9m, endedAt: At(23, 0));

        var history = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour);

        Assert.Equal(1, history.Totals.Sessions);
        Assert.Equal(100, history.Totals.TotalTokens);
        Assert.Equal(100, history.Buckets.Sum(b => b.Totals.TotalTokens));
    }

    [Fact]
    public async Task TheAncestorFilter_CoversTheIssueItselfAndEveryDescendant()
    {
        var db = await NewAsync();
        var (epic, story, task) = await ThreeLevelsAsync(db);
        var elsewhere = await FileAsync(db, "epic", null);

        await SpendAsync(db, epic, tokens: 1_000, usd: 0m, endedAt: At(0, 30));
        await SpendAsync(db, story, tokens: 20_000, usd: 0m, endedAt: At(1, 30));
        await SpendAsync(db, task, tokens: 300_000, usd: 0m, endedAt: At(2, 30));
        await SpendAsync(db, elsewhere, tokens: 7_000_000, usd: 0m, endedAt: At(3, 30));

        var scoped = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour, ancestorId: epic);

        // The epic's own session is money no child holds, and a graph that
        // dropped it would disagree with the meter on the issue page.
        Assert.Equal(3, scoped.Totals.Sessions);
        Assert.Equal(321_000, scoped.Totals.TotalTokens);

        var whole = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour);
        Assert.Equal(4, whole.Totals.Sessions);
    }

    [Fact]
    public async Task AnEmptyLog_IsAFullRunOfZeroedBucketsWithNoBounds()
    {
        var db = await NewAsync();

        var history = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour);

        Assert.Equal(6, history.Buckets.Count);
        Assert.All(history.Buckets, b => Assert.Equal(0, b.Totals.Sessions));
        Assert.Equal(0, history.Totals.Sessions);
        Assert.Null(history.FirstSessionAt);
        Assert.Null(history.LastSessionAt);
    }

    [Fact]
    public async Task TheBounds_AreTheFilteredLogsOwnEvenWhenTheRangeMissesThemBoth()
    {
        var db = await NewAsync();
        var (epic, story, _) = await ThreeLevelsAsync(db);
        var elsewhere = await FileAsync(db, "epic", null);

        var earliest = At(1, 0).AddDays(-30);
        var latest = At(1, 0).AddDays(30);
        await SpendAsync(db, epic, tokens: 100, usd: 0m, endedAt: earliest);
        await SpendAsync(db, story, tokens: 100, usd: 0m, endedAt: latest);

        // Another epic's session, well outside the filtered population's span.
        await SpendAsync(db, elsewhere, tokens: 100, usd: 0m, endedAt: At(1, 0).AddDays(-90));

        var history = await SeriesAsync(db, At(0, 0), At(6, 0), WorkLogBucketSize.Hour, ancestorId: epic);

        // Nothing ran in the range asked for, and something has certainly run:
        // the two answers are different and a page needs both.
        Assert.Equal(0, history.Totals.Sessions);
        Assert.Equal(earliest, history.FirstSessionAt);
        Assert.Equal(latest, history.LastSessionAt);
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 3, 0, 0, TimeSpan.Zero);

    /// <summary>An hour and a minute of the day the tests all happen on.</summary>
    private static DateTimeOffset At(int hour, int minute) =>
        new(2026, 9, 7, hour, minute, 0, TimeSpan.Zero);

    /// <summary>Align and fold in one call, which is what the endpoint does.</summary>
    private static Task<WorkLogHistoryDto> SeriesAsync(
        HatchContext db,
        DateTimeOffset from,
        DateTimeOffset to,
        WorkLogBucketSize bucket,
        int offsetMinutes = 0,
        long? ancestorId = null) =>
        WorkLogRollup.SeriesAsync(
            db, WorkLogRollup.Align(from, to, bucket, offsetMinutes), bucket, ancestorId, default);

    private static async Task<HatchContext> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        db.Add(new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now });
        db.Add(new EfHatchStatus { Name = "inbox", SortOrder = 10 });
        await db.SaveChangesAsync();

        return db;
    }

    /// <summary>An epic, a story under it, and a task under that.</summary>
    private static async Task<(long Epic, long Story, long Task)> ThreeLevelsAsync(HatchContext db)
    {
        var epic = await FileAsync(db, "epic", null);
        var story = await FileAsync(db, "story", epic);
        return (epic, story, await FileAsync(db, "task", story));
    }

    private static async Task<long> FileAsync(HatchContext db, string type, long? parentId)
    {
        var project = await db.Projects.FirstAsync();
        var status = await db.Statuses.FirstAsync();

        var issue = new EfHatchIssue
        {
            ProjectId = project.Id,
            Number = await db.Issues.CountAsync() + 1,
            Type = type,
            Title = type,
            StatusId = status.Id,
            ParentId = parentId,
            Rank = 1024,
            CreatedBy = "operator",
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        db.Issues.Add(issue);
        await db.SaveChangesAsync();

        return issue.Id;
    }

    /// <summary>One session against one issue, its tokens all in the input column.</summary>
    private static async Task SpendAsync(
        HatchContext db, long issueId, long tokens, decimal usd, bool isError = false, DateTimeOffset? endedAt = null)
    {
        db.WorkLog.Add(Entry(db, issueId, tokens, usd, isError, endedAt ?? Now));
        await db.SaveChangesAsync();
    }

    private static EfHatchWorkLogEntry Entry(
        HatchContext db, long issueId, long tokens, decimal usd, bool isError, DateTimeOffset endedAt) =>
        new()
        {
            IssueId = issueId,
            SessionId = $"session-{db.WorkLog.Local.Count}-{issueId}-{tokens}",
            StartedAt = endedAt,
            EndedAt = endedAt,
            DurationMs = 1_000,
            IsError = isError,
            Turns = 1,
            CostUsd = usd,
            InputTokens = tokens,
            CreatedAt = Now,
        };

    private static EfHatchWorkLogEntry Entry(
        long issueId, string sessionId, long input, long output, long cacheCreation, long cacheRead, decimal usd) =>
        new()
        {
            IssueId = issueId,
            SessionId = sessionId,
            StartedAt = Now,
            EndedAt = Now,
            DurationMs = 1_000,
            Turns = 1,
            CostUsd = usd,
            InputTokens = input,
            OutputTokens = output,
            CacheCreationTokens = cacheCreation,
            CacheReadTokens = cacheRead,
            CreatedAt = Now,
        };
}
