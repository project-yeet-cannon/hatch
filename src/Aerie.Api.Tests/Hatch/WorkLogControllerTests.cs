using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The work log read across issues: what a caller may ask for, what the server
/// decides when they ask for nothing, and the six things it refuses in a
/// sentence.
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
        public async Task SpendAsync(
            IssueDto issue, long tokens, DateTimeOffset endedAt, decimal usd = 0m, bool isError = false)
        {
            IssueKey.TryParse(issue.Key, out var projectKey, out var number);
            var issueId = await Db.Issues.AsNoTracking().WithKey(projectKey, number).Select(i => i.Id).FirstAsync();

            Db.WorkLog.Add(new EfHatchWorkLogEntry
            {
                IssueId = issueId,
                SessionId = $"session-{Db.WorkLog.Local.Count}-{issueId}-{tokens}",
                StartedAt = endedAt,
                EndedAt = endedAt,
                DurationMs = 1_000,
                IsError = isError,
                Turns = 1,
                CostUsd = usd,
                InputTokens = tokens,
                CreatedAt = Now,
            });
            await Db.SaveChangesAsync();
        }

        public async Task<WorkLogHistoryDto> HistoryAsync(
            string? from = null,
            string? to = null,
            string? bucket = null,
            int offsetMinutes = 0,
            string? ancestorKey = null) =>
            Value(await History.GetHistory(from, to, bucket, offsetMinutes, ancestorKey, default));

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

        var aerie = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        db.Add(aerie);
        db.Add(new EfHatchStatus { Name = "inbox", SortOrder = 10 });
        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(Now);

        var caller = new StubCallerIdentity
        {
            Key = new EfApiKey
            {
                Name = "hatch",
                Hash = [1],
                Prefix = "aerie_ak_x",
                Scopes = [ApiKeyScopes.Hatch],
                CreatedAt = Now,
            },
        };

        return new Harness
        {
            History = new WorkLogController(db, time),
            Issues = new IssuesController(db, new RankService(db), TestClaims.With(), caller, time),
            Db = db,
            ProjectId = aerie.Id,
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

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(Person?.Name ?? Key?.Name ?? CallerIdentity.Unattributed);
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
