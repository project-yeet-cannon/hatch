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

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 3, 0, 0, TimeSpan.Zero);

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
    private static async Task SpendAsync(HatchContext db, long issueId, long tokens, decimal usd, bool isError = false)
    {
        db.WorkLog.Add(Entry(db, issueId, tokens, usd, isError));
        await db.SaveChangesAsync();
    }

    private static EfHatchWorkLogEntry Entry(HatchContext db, long issueId, long tokens, decimal usd, bool isError) =>
        new()
        {
            IssueId = issueId,
            SessionId = $"session-{db.WorkLog.Local.Count}-{issueId}-{tokens}",
            StartedAt = Now,
            EndedAt = Now,
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
