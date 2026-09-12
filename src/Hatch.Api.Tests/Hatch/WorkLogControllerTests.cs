using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Modules.Hatch;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// The work log read across issues - the graph's buckets and the leaderboard's
/// ranked sessions: what a caller may ask for, what the server decides when they
/// ask for nothing, and everything it refuses in a sentence.
/// </summary>
public class WorkLogControllerTests
{
    // ---- Choosing what to look at ----

    [Fact]
    public async Task NoRangeAtAll_IsTheFourteenDaysEndingNow()
    {
        var h = await NewAsync();

        var history = await h.HistoryAsync();

        // Fourteen days back from Now, snapped outward onto whole days - so the
        // range, the buckets and the totals describe one window.
        Assert.Equal("day", history.Bucket);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero), history.From);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero), history.To);
        Assert.Equal(15, history.Buckets.Count);
        Assert.Equal(history.From, history.Buckets[0].Start);
        Assert.Equal(history.To, history.Buckets[^1].End);
    }

    [Fact]
    public async Task OneBoundOnItsOwn_GetsTheOthersDefault()
    {
        var h = await NewAsync();

        var since = await h.HistoryAsync(from: "2026-09-06T03:00:00Z");
        var until = await h.HistoryAsync(to: "2026-09-06T03:00:00Z");

        // Named `from`, so `to` is now; named `to`, so `from` is fourteen days
        // before it. Neither is a refusal.
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 3, 0, 0, TimeSpan.Zero), since.Buckets[^1].End);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 0, 0, 0, TimeSpan.Zero), until.From);
    }

    [Fact]
    public async Task AnAbsentBucket_IsHourlyUpToTwoDaysAndDailyPastIt()
    {
        var h = await NewAsync();

        var day = await h.HistoryAsync(from: "2026-09-06T03:00:00Z", to: "2026-09-07T03:00:00Z");
        var month = await h.HistoryAsync(from: "2026-08-08T03:00:00Z", to: "2026-09-07T03:00:00Z");

        // Named back in the answer, so a client labels its axis from what the
        // server did rather than from what it asked for.
        Assert.Equal("hour", day.Bucket);
        Assert.Equal(24, day.Buckets.Count);
        Assert.Equal("day", month.Bucket);
    }

    [Fact]
    public async Task AnExplicitBucket_IsHonouredInEitherCase()
    {
        var h = await NewAsync();

        var shouted = await h.HistoryAsync(from: "2026-09-06T03:00:00Z", to: "2026-09-07T03:00:00Z", bucket: "HOUR");
        var daily = await h.HistoryAsync(from: "2026-09-06T03:00:00Z", to: "2026-09-07T03:00:00Z", bucket: " Day ");

        Assert.Equal("hour", shouted.Bucket);
        Assert.Equal("day", daily.Bucket);
    }

    [Fact]
    public async Task AnOffset_MovesTheDailyGridToTheReadersMidnight()
    {
        var h = await NewAsync();

        var history = await h.HistoryAsync(
            from: "2026-09-01T12:00:00Z", to: "2026-09-04T12:00:00Z", bucket: "day", offsetMinutes: -300);

        // Midnight five hours west, not UTC midnight: an overnight run split
        // across UTC midnight is two half-nights nobody worked.
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 5, 0, 0, TimeSpan.Zero), history.From);
        Assert.All(history.Buckets, b => Assert.Equal(5, b.Start.UtcDateTime.Hour));
    }

    // ---- Narrowing to one project ----

    [Fact]
    public async Task AnAncestorKey_FiltersToThatIssueAndEverythingBeneathIt()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the effort");
        var story = await h.FileAsync("story", "under it", parentKey: epic.Key);
        var elsewhere = await h.FileAsync("epic", "another effort");

        await h.SpendAsync(epic, 1_000, Now.AddHours(-2));
        await h.SpendAsync(story, 20_000, Now.AddHours(-1));
        await h.SpendAsync(elsewhere, 7_000_000, Now.AddHours(-1));

        var scoped = await h.HistoryAsync(ancestorKey: epic.Key);
        var whole = await h.HistoryAsync();

        // The named issue's own sessions are in it: a planning session run
        // against the epic is money no child holds.
        Assert.Equal(2, scoped.Totals.Sessions);
        Assert.Equal(21_000, scoped.Totals.TotalTokens);
        Assert.Equal(3, whole.Totals.Sessions);
    }

    [Fact]
    public async Task AnAncestorKeyNamingNothing_IsRefusedRatherThanAnsweredAsEmpty()
    {
        var h = await NewAsync();

        Assert.Equal("there is no AER-999", await h.RefusalAsync(ancestorKey: "AER-999"));
        Assert.Equal("there is no nonsense", await h.RefusalAsync(ancestorKey: "nonsense"));
    }

    // ---- When there is nothing yet ----

    [Fact]
    public async Task AnEmptyLog_IsZeroedBucketsAndNoBounds()
    {
        var h = await NewAsync();

        var history = await h.HistoryAsync(from: "2026-09-06T00:00:00Z", to: "2026-09-07T00:00:00Z");

        // The ordinary state on the first day, not a fault.
        Assert.Equal(24, history.Buckets.Count);
        Assert.All(history.Buckets, b => Assert.Equal(0, b.Totals.Sessions));
        Assert.Null(history.FirstSessionAt);
        Assert.Null(history.LastSessionAt);
    }

    [Fact]
    public async Task ARangeBeforeTheFirstSession_AnswersWithTheBoundsStillFilledIn()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var ended = Now.AddHours(-1);
        await h.SpendAsync(issue, 1_000, ended);

        var history = await h.HistoryAsync(from: "2026-01-01T00:00:00Z", to: "2026-01-03T00:00:00Z");

        // "Nothing ran in the range you asked for" and "nothing has ever run"
        // are different answers, and a page needs to tell them apart.
        Assert.Equal(0, history.Totals.Sessions);
        Assert.Equal(ended, history.FirstSessionAt);
        Assert.Equal(ended, history.LastSessionAt);
    }

    // ---- Spend over time ----

    [Fact]
    public async Task TheBuckets_ComeBackOldestFirstAndAddUpToTheTotals()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SpendAsync(issue, 1_000, new DateTimeOffset(2026, 9, 7, 0, 30, 0, TimeSpan.Zero), usd: 0.10m);
        await h.SpendAsync(issue, 20_000, new DateTimeOffset(2026, 9, 7, 2, 15, 0, TimeSpan.Zero), usd: 2.00m);
        await h.SpendAsync(issue, 300_000, new DateTimeOffset(2026, 9, 7, 2, 45, 0, TimeSpan.Zero), usd: 30.00m, isError: true);

        var history = await h.HistoryAsync(from: "2026-09-07T00:00:00Z", to: "2026-09-07T03:00:00Z");

        Assert.Equal(
            history.Buckets.Select(b => b.Start).OrderBy(s => s),
            history.Buckets.Select(b => b.Start));

        Assert.Equal([1, 0, 2], history.Buckets.Select(b => b.Totals.Sessions));
        Assert.Equal(3, history.Totals.Sessions);
        Assert.Equal(1, history.Totals.Errors);
        Assert.Equal(321_000, history.Totals.TotalTokens);
        Assert.Equal(32.10m, history.Totals.CostUsd);
        Assert.Equal(history.Totals.CostUsd, history.Buckets.Sum(b => b.Totals.CostUsd));
    }

    // ---- What it refuses ----

    [Fact]
    public async Task AnUnknownBucket_IsRefusedWithTheSentence()
    {
        var h = await NewAsync();

        Assert.Equal("a bucket is hour or day - not \"week\"", await h.RefusalAsync(bucket: "week"));
    }

    [Fact]
    public async Task AnUnparseableBound_IsRefusedInTheHousesOwnWords()
    {
        var h = await NewAsync();

        Assert.Equal(
            "a range bound is an instant (2026-09-12T17:00:00Z) - not \"last tuesday\"",
            await h.RefusalAsync(from: "last tuesday"));
        Assert.Equal(
            "a range bound is an instant (2026-09-12T17:00:00Z) - not \"09/12/2026\"",
            await h.RefusalAsync(to: "09/12/2026"));
    }

    [Fact]
    public async Task AnInvertedRange_IsRefusedAndAnEmptyOneIsOneBucket()
    {
        var h = await NewAsync();

        Assert.Equal(
            "a range ends before it begins",
            await h.RefusalAsync(from: "2026-09-07T03:00:00Z", to: "2026-09-06T03:00:00Z"));

        // Equal is legal: one bucket rather than none.
        var single = await h.HistoryAsync(from: "2026-09-07T03:00:00Z", to: "2026-09-07T03:00:00Z");
        Assert.Single(single.Buckets);
    }

    [Fact]
    public async Task TooManyBuckets_IsRefusedBeforeAnythingIsScanned()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.SpendAsync(issue, 1_000, Now.AddHours(-1));

        var refusal = await h.RefusalAsync(from: "2026-06-01T00:00:00Z", to: "2026-09-07T00:00:00Z", bucket: "hour");

        Assert.Contains("1000", refusal);
        Assert.Contains("ask for daily buckets, or a shorter range", refusal);

        // Untouched: "how far back does this go" is a question the answer should
        // settle rather than a licence to scan the table.
        Assert.Single(h.Db.WorkLog);
    }

    [Fact]
    public async Task AnOffsetThatIsNotAnOffset_IsRefused()
    {
        var h = await NewAsync();

        Assert.Equal(
            "an offset from UTC is minutes between -1440 and 1440 - not 9999",
            await h.RefusalAsync(offsetMinutes: 9999));
    }

    // ---- The sessions, ranked ----

    [Fact]
    public async Task TheSessionsRange_IsTheFourteenDaysEndingNowAndIsNotSnapped()
    {
        var h = await NewAsync();

        var sessions = await h.SessionsAsync();

        // Echoed exactly as resolved, unlike `history`, which snaps both bounds
        // outward onto the bucket grid it draws on. There is no grid here.
        Assert.Equal(Now.AddDays(-14), sessions.From);
        Assert.Equal(Now, sessions.To);
        Assert.Equal("tokens", sessions.Sort);
    }

    [Fact]
    public async Task OneSessionsBoundOnItsOwn_GetsTheOthersDefault()
    {
        var h = await NewAsync();

        var since = await h.SessionsAsync(from: "2026-09-06T03:00:00Z");
        var until = await h.SessionsAsync(to: "2026-09-06T03:00:00Z");

        Assert.Equal(Now, since.To);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 3, 0, 0, TimeSpan.Zero), until.From);
    }

    [Fact]
    public async Task TheDefaultSort_IsTokensDescending()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SpendAsync(issue, 1_000, Now.AddHours(-3));
        await h.SpendAsync(issue, 300_000, Now.AddHours(-2));
        await h.SpendAsync(issue, 20_000, Now.AddHours(-1));

        var sessions = await h.SessionsAsync();

        Assert.Equal([300_000, 20_000, 1_000], sessions.Sessions.Select(s => s.TotalTokens));
    }

    [Fact]
    public async Task CostRanksOnTheDollars_AndEndedIsNewestFirst()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SpendAsync(issue, 300_000, Now.AddHours(-3), usd: 0.10m);
        await h.SpendAsync(issue, 1_000, Now.AddHours(-2), usd: 30.00m);
        await h.SpendAsync(issue, 20_000, Now.AddHours(-1), usd: 2.00m);

        var byCost = await h.SessionsAsync(sort: "cost");
        var byEnded = await h.SessionsAsync(sort: "ended");

        Assert.Equal("cost", byCost.Sort);
        Assert.Equal([30.00m, 2.00m, 0.10m], byCost.Sessions.Select(s => s.CostUsd));
        Assert.Equal("ended", byEnded.Sort);
        Assert.Equal(
            byEnded.Sessions.Select(s => s.EndedAt).OrderByDescending(e => e),
            byEnded.Sessions.Select(s => s.EndedAt));
    }

    [Fact]
    public async Task TiedSessions_BreakOnWhenTheyEndedAndThenOnId()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var older = await h.SpendAsync(issue, 5_000, Now.AddHours(-2));
        var first = await h.SpendAsync(issue, 5_000, Now.AddHours(-1));
        var second = await h.SpendAsync(issue, 5_000, Now.AddHours(-1));

        var sessions = await h.SessionsAsync();

        // Two reads of one filter come back in one order, which is what makes a
        // capped list stable across a refresh.
        Assert.Equal([second, first, older], sessions.Sessions.Select(s => s.Id));
    }

    [Fact]
    public async Task ASort_IsTakenInEitherCaseAndWithSpaceAroundIt()
    {
        var h = await NewAsync();

        Assert.Equal("cost", (await h.SessionsAsync(sort: "COST")).Sort);
        Assert.Equal("ended", (await h.SessionsAsync(sort: " Ended ")).Sort);
        Assert.Equal("tokens", (await h.SessionsAsync(sort: "  ")).Sort);
    }

    [Fact]
    public async Task TheCap_LimitsTheRowsAndNeverTheTotals()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        for (var i = 0; i < 120; i++) await h.SpendAsync(issue, 1_000, Now.AddMinutes(-i - 1));

        var sessions = await h.SessionsAsync();

        // A hundred rows under a total of 120 is a cap, and the page says so -
        // the number itself is never a sample.
        Assert.Equal(100, sessions.Sessions.Count);
        Assert.Equal(120, sessions.Totals.Sessions);
        Assert.Equal(120_000, sessions.Totals.TotalTokens);
    }

    [Fact]
    public async Task AnExplicitLimit_IsHonouredAtEitherEnd()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        for (var i = 0; i < 3; i++) await h.SpendAsync(issue, 1_000, Now.AddMinutes(-i - 1));

        Assert.Single((await h.SessionsAsync(limit: 1)).Sessions);
        Assert.Equal(3, (await h.SessionsAsync(limit: 500)).Sessions.Count);
    }

    [Fact]
    public async Task AnAncestorKeyOnTheSessions_CoversTheIssueItselfAndEveryDepthBeneathIt()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the effort");
        var story = await h.FileAsync("story", "under it", parentKey: epic.Key);
        var task = await h.FileAsync("task", "under that", parentKey: story.Key);
        var elsewhere = await h.FileAsync("epic", "another effort");

        await h.SpendAsync(epic, 1_000, Now.AddHours(-3));
        await h.SpendAsync(story, 20_000, Now.AddHours(-2));
        await h.SpendAsync(task, 300_000, Now.AddHours(-1));
        await h.SpendAsync(elsewhere, 7_000_000, Now.AddHours(-1));

        var scoped = await h.SessionsAsync(ancestorKey: epic.Key);

        // The named issue's own sessions are in it: a planning session run
        // against the epic is money no child holds.
        Assert.Equal(3, scoped.Totals.Sessions);
        Assert.Equal(321_000, scoped.Totals.TotalTokens);
        Assert.DoesNotContain(elsewhere.Key, scoped.Sessions.Select(s => s.IssueKey));
    }

    [Fact]
    public async Task AnErroredSession_IsCountedAndItsSpendCounts()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SpendAsync(issue, 1_000, Now.AddHours(-2), usd: 0.10m);
        await h.SpendAsync(issue, 300_000, Now.AddHours(-1), usd: 30.00m, isError: true);

        var sessions = await h.SessionsAsync();

        // It ran, and it was billed for running.
        Assert.Equal(2, sessions.Totals.Sessions);
        Assert.Equal(1, sessions.Totals.Errors);
        Assert.Equal(30.10m, sessions.Totals.CostUsd);
        Assert.True(sessions.Sessions[0].IsError);
    }

    [Fact]
    public async Task EachRow_NamesTheIssueItWasRunAgainstAndWhatItSaidOfItself()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync("story", "the leaderboard");

        await h.SpendAsync(issue, 20_000, Now.AddHours(-2), title: "Broke the ranking out of the rollup", turns: 41);
        await h.SpendAsync(issue, 1_000, Now.AddHours(-1));

        var sessions = await h.SessionsAsync();

        var described = sessions.Sessions[0];
        Assert.Equal(issue.Key, described.IssueKey);
        Assert.Equal("the leaderboard", described.IssueTitle);
        Assert.Equal("Broke the ranking out of the rollup", described.Title);
        Assert.True(described.Described);
        Assert.Equal(41, described.Turns);
        Assert.Equal(1_000, described.DurationMs);

        // A session that never said what it did is marked, not blanked.
        Assert.False(sessions.Sessions[1].Described);
        Assert.Null(sessions.Sessions[1].Title);
    }

    [Fact]
    public async Task ARangeWithNothingInIt_IsAnEmptyListAndZeroedTotals()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.SpendAsync(issue, 1_000, Now.AddHours(-1));

        var sessions = await h.SessionsAsync(from: "2026-01-01T00:00:00Z", to: "2026-01-03T00:00:00Z");

        Assert.Empty(sessions.Sessions);
        Assert.Equal(0, sessions.Totals.Sessions);
        Assert.Equal(0m, sessions.Totals.CostUsd);
    }

    [Fact]
    public async Task TheTwoBounds_IgnoreTheRangeAndRespectTheAncestor()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the effort");
        var elsewhere = await h.FileAsync("epic", "another effort");

        var first = Now.AddDays(-40);
        var last = Now.AddHours(-1);
        await h.SpendAsync(epic, 1_000, first);
        await h.SpendAsync(epic, 1_000, last);
        await h.SpendAsync(elsewhere, 1_000, Now.AddDays(-90));

        var scoped = await h.SessionsAsync(ancestorKey: epic.Key);
        var empty = await h.SessionsAsync(ancestorKey: (await h.FileAsync()).Key);

        // Outside the fourteen-day default on both sides, and still reported:
        // it is how a page tells "nothing has ever been logged" from "nothing
        // ran in the range you asked for".
        Assert.Equal(first, scoped.FirstSessionAt);
        Assert.Equal(last, scoped.LastSessionAt);

        // The range still applies to everything else: only the session inside
        // the fourteen-day default is counted.
        Assert.Equal(1, scoped.Totals.Sessions);
        Assert.Null(empty.FirstSessionAt);
        Assert.Null(empty.LastSessionAt);
    }

    // ---- What the sessions read refuses ----

    [Fact]
    public async Task TheSessionsRead_RefusesABadBoundAndAnInvertedRangeInTheSameWords()
    {
        var h = await NewAsync();

        Assert.Equal(
            "a range bound is an instant (2026-09-12T17:00:00Z) - not \"last tuesday\"",
            await h.SessionRefusalAsync(from: "last tuesday"));
        Assert.Equal(
            "a range ends before it begins",
            await h.SessionRefusalAsync(from: "2026-09-07T03:00:00Z", to: "2026-09-06T03:00:00Z"));
        Assert.Equal("there is no AER-999", await h.SessionRefusalAsync(ancestorKey: "AER-999"));
    }

    [Fact]
    public async Task AnUnknownSort_IsRefusedWithTheSentence()
    {
        var h = await NewAsync();

        Assert.Equal("a sort is tokens, cost or ended - not \"turns\"", await h.SessionRefusalAsync(sort: "turns"));
    }

    [Fact]
    public async Task ALimitOutsideTheCap_IsRefusedWithTheCapInIt()
    {
        var h = await NewAsync();

        Assert.Equal("a limit is between 1 and 500 - not 0", await h.SessionRefusalAsync(limit: 0));
        Assert.Equal("a limit is between 1 and 500 - not -1", await h.SessionRefusalAsync(limit: -1));
        Assert.Equal("a limit is between 1 and 500 - not 501", await h.SessionRefusalAsync(limit: 501));
    }

    // ---- Who may read it ----

    [Fact]
    public void TheRoute_AcceptsTheHatchScope()
    {
        var guard = typeof(WorkLogController)
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .Single();

        Assert.Equal(ApiKeyScopes.Hatch, guard.AcceptScope);
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 3, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required WorkLogController History { get; init; }

        public required IssuesController Issues { get; init; }

        public required HatchContext Db { get; init; }

        public required int ProjectId { get; init; }

        public async Task<IssueDto> FileAsync(string type = "task", string title = "a thing", string? parentKey = null) =>
            Created(await Issues.CreateIssue(
                new IssueCreateRequest(ProjectId, type, title, null, parentKey, null, null), default));

        /// <summary>
        /// A row straight into the context rather than through the writer, so
        /// the test chooses when the session ended.
        /// </summary>
        public async Task<long> SpendAsync(
            IssueDto issue,
            long tokens,
            DateTimeOffset endedAt,
            decimal usd = 0m,
            bool isError = false,
            string? title = null,
            bool described = false,
            long durationMs = 1_000,
            int turns = 1)
        {
            IssueKey.TryParse(issue.Key, out var projectKey, out var number);
            var issueId = await Db.Issues.AsNoTracking().WithKey(projectKey, number).Select(i => i.Id).FirstAsync();

            var entry = new EfHatchWorkLogEntry
            {
                IssueId = issueId,
                SessionId = $"session-{Db.WorkLog.Local.Count}-{issueId}-{tokens}",
                StartedAt = endedAt.AddMilliseconds(-durationMs),
                EndedAt = endedAt,
                DurationMs = durationMs,
                Title = title,
                // What the writer would have derived: a session that named
                // itself described itself.
                Described = described || title is not null,
                IsError = isError,
                Turns = turns,
                CostUsd = usd,
                InputTokens = tokens,
                CreatedAt = Now,
            };

            Db.WorkLog.Add(entry);
            await Db.SaveChangesAsync();
            return entry.Id;
        }

        public async Task<WorkLogHistoryDto> HistoryAsync(
            string? from = null,
            string? to = null,
            string? bucket = null,
            int offsetMinutes = 0,
            string? ancestorKey = null) =>
            Value(await History.GetHistory(from, to, bucket, offsetMinutes, ancestorKey, default));

        public async Task<WorkLogSessionsDto> SessionsAsync(
            string? from = null,
            string? to = null,
            string? ancestorKey = null,
            string? sort = null,
            int limit = 100) =>
            Value(await History.GetSessions(from, to, ancestorKey, sort, limit, default));

        public async Task<string> SessionRefusalAsync(
            string? from = null,
            string? to = null,
            string? ancestorKey = null,
            string? sort = null,
            int limit = 100)
        {
            var result = await History.GetSessions(from, to, ancestorKey, sort, limit, default);
            return Assert.IsType<BadRequestObjectResult>(result.Result).Value?.ToString() ?? "";
        }

        public async Task<string> RefusalAsync(
            string? from = null,
            string? to = null,
            string? bucket = null,
            int offsetMinutes = 0,
            string? ancestorKey = null)
        {
            var result = await History.GetHistory(from, to, bucket, offsetMinutes, ancestorKey, default);
            return Assert.IsType<BadRequestObjectResult>(result.Result).Value?.ToString() ?? "";
        }
    }

    private static async Task<Harness> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var hatch = new EfHatchProject { Key = "AER", Name = "Hatch", CreatedAt = Now };
        db.Add(hatch);
        db.Add(new EfHatchStatus { Name = "inbox", SortOrder = 10 });
        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(Now);

        var caller = new StubCallerIdentity
        {
            Key = new EfApiKey
            {
                Name = "hatch",
                Hash = [1],
                Prefix = "hatch_ak_x",
                Scopes = [ApiKeyScopes.Hatch],
                CreatedAt = Now,
            },
        };

        return new Harness
        {
            History = new WorkLogController(db, time),
            Issues = new IssuesController(db, new RankService(db), new StubActorDirectory(), TestClaims.With(), caller, time),
            Db = db,
            ProjectId = hatch.Id,
        };
    }

    /// <summary>Whoever the test says is holding the phone - see IssueWorkLogControllerTests.</summary>
    private sealed class StubCallerIdentity : ICallerIdentity
    {
        public EfPerson? Person { get; set; }

        public EfApiKey? Key { get; set; }

        public Task<EfAuthGrant?> GrantAsync(CancellationToken ct) => Task.FromResult<EfAuthGrant?>(null);

        public Task<Guid?> PersonIdAsync(CancellationToken ct) => Task.FromResult(Person?.Id);

        public Task<EfPerson?> PersonAsync(CancellationToken ct) => Task.FromResult(Person);

        public Task<EfApiKey?> ApiKeyAsync(CancellationToken ct) => Task.FromResult(Key);

        /// <summary>Local mode's third lane: the person at the machine, or a runner that named itself. Null is every install with a wall.</summary>
        public Actor? Local { get; set; }

        public Task<Actor?> LocalAsync(CancellationToken ct) => Task.FromResult(Local);

        public Task<bool> IsProgramAsync(CancellationToken ct) =>
            Task.FromResult(Key is not null || Local is { Kind: ActorKind.Key });

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(Person?.Name ?? Key?.Name ?? Local?.Name ?? CallerIdentity.Unattributed);
    }

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {Reason(result.Result)}");

    private static T Created<T>(ActionResult<T> result) =>
        result.Result is CreatedAtActionResult created
            ? (T)created.Value!
            : result.Value ?? throw new InvalidOperationException($"expected a created value, got {Reason(result.Result)}");

    private static string Reason(IActionResult? result) => result switch
    {
        ObjectResult o => o.Value?.ToString() ?? $"{o.StatusCode}",
        StatusCodeResult s => s.StatusCode.ToString(),
        null => "no result",
        _ => result.GetType().Name,
    };
}
