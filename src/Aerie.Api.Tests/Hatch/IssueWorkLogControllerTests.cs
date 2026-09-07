using System.Reflection;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The one verb that writes a meter reading: what it derives rather than
/// accepts, what it does when the same session reports twice, and who it
/// refuses.
/// </summary>
public class IssueWorkLogControllerTests
{
    // ---- Writing a row ----

    [Fact]
    public async Task ASession_LeavesARowCarryingEverythingItReported()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var entry = await h.PostAsync(issue.Key, Reported() with
        {
            Title = "Wired the battery into the nav",
            Summary = "Read the account's headroom server-side and drew the ring.",
            Turns = 41,
            DurationMs = 842_000,
            CostUsd = 3.41m,
        });

        Assert.Equal(Session, entry.SessionId);
        Assert.Equal("Wired the battery into the nav", entry.Title);
        Assert.Equal(842_000, entry.DurationMs);
        Assert.Equal(41, entry.Turns);
        Assert.Equal(3.41m, entry.CostUsd);
        Assert.False(entry.IsError);
        Assert.True(entry.Described);

        // Wall clock either side of the CLI, kept as sent rather than reconciled
        // with the duration the session reported.
        Assert.Equal(Started, entry.StartedAt);
        Assert.Equal(Ended, entry.EndedAt);
    }

    [Fact]
    public async Task TheFourCounts_AreTheSumOfTheBreakdownAndNotSentByTheCaller()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var entry = await h.PostAsync(issue.Key, Reported() with
        {
            Models =
            [
                new WorkLogModelUseDto("claude-opus-5", 900, 280, 3_200, 59_000, 0.0140m),
                new WorkLogModelUseDto("claude-haiku-4-5", 47, 1, 14, 57, 0.0006m),
            ],
        });

        Assert.Equal(947, entry.InputTokens);
        Assert.Equal(281, entry.OutputTokens);
        Assert.Equal(3_214, entry.CacheCreationTokens);
        Assert.Equal(59_057, entry.CacheReadTokens);

        // Carried rather than left for the client, so the headline figure has
        // one definition - and equal to the breakdown by construction.
        Assert.Equal(947 + 281 + 3_214 + 59_057, entry.TotalTokens);

        Assert.Equal(["claude-opus-5", "claude-haiku-4-5"], entry.Models.Select(m => m.Model));
        Assert.Equal(0.0140m, entry.Models[0].CostUsd);
    }

    [Fact]
    public async Task ARunThatDiedBeforeTheAccountingArrived_StillGetsItsRow()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var entry = await h.PostAsync(issue.Key, Reported() with
        {
            Models = null,
            IsError = true,
            CostUsd = 0.0012m,
        });

        // Zero tokens and whatever it cost: a session that ended badly still had
        // an id and still cost something, and losing that would be the wrong
        // trade.
        Assert.Equal(0, entry.TotalTokens);
        Assert.Empty(entry.Models);
        Assert.Equal(0.0012m, entry.CostUsd);
        Assert.True(entry.IsError);
    }

    [Fact]
    public async Task ASessionThatNeverSaidWhatItDid_IsMarkedUndescribed()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var entry = await h.PostAsync(issue.Key, Reported() with { Title = "   ", Summary = null });

        // On the wire, not inferred by a client from an empty title.
        Assert.False(entry.Described);
        Assert.Null(entry.Title);
        Assert.Null(entry.Summary);

        // And its metrics are intact, which is the whole reason the row exists.
        Assert.Equal(Session, entry.SessionId);
        Assert.Equal(4_888, entry.DurationMs);
    }

    [Fact]
    public async Task ATitleOrSummaryOnItsOwn_IsEnoughToCountAsDescribed()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        Assert.True((await h.PostAsync(issue.Key, Reported() with { Title = "did a thing", Summary = null })).Described);
        Assert.True((await h.PostAsync(issue.Key, Reported("other") with { Title = null, Summary = "did a thing" })).Described);
    }

    [Fact]
    public async Task OverLongText_IsClippedRatherThanRefused()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var entry = await h.PostAsync(issue.Key, Reported() with
        {
            Title = new string('t', EfHatchWorkLogEntry.MaxTitleLength + 40),
            Summary = new string('s', EfHatchWorkLogEntry.MaxSummaryLength + 400),
        });

        // The hundred words are an instruction to the session, not a validation
        // on the row: refusing a long summary would lose an evening's spend to a
        // style note.
        Assert.Equal(EfHatchWorkLogEntry.MaxTitleLength, entry.Title!.Length);
        Assert.Equal(EfHatchWorkLogEntry.MaxSummaryLength, entry.Summary!.Length);
        Assert.True(entry.Described);
    }

    // ---- Reporting twice ----

    [Fact]
    public async Task TheSameSessionPostedTwice_LeavesOneEntryAndSucceedsBothTimes()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var first = await h.PostAsync(issue.Key, Reported() with { Title = "first go", Turns = 3 });
        var second = await h.PostAsync(issue.Key, Reported() with { Title = "second go", Turns = 9 });

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("second go", second.Title);
        Assert.Equal(9, second.Turns);
        Assert.Single(await h.RowsAsync(issue.Key));
    }

    [Fact]
    public async Task TwoSessionsOnOneIssue_AreTwoEntries()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.PostAsync(issue.Key, Reported());
        await h.PostAsync(issue.Key, Reported("a-second-session"));

        Assert.Equal(2, (await h.RowsAsync(issue.Key)).Count);
    }

    [Fact]
    public async Task OneSessionAcrossTwoIssues_IsARowOnEach()
    {
        var h = await NewAsync();
        var one = await h.FileAsync(title: "one");
        var two = await h.FileAsync(title: "two");

        // Uniqueness is per issue and not per session: an interrupted run
        // resumed against a second ticket spent money on both.
        await h.PostAsync(one.Key, Reported());
        await h.PostAsync(two.Key, Reported());

        Assert.Single(await h.RowsAsync(one.Key));
        Assert.Single(await h.RowsAsync(two.Key));
    }

    // ---- Reading it back ----

    [Fact]
    public async Task TheLog_ComesBackNewestFirst()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.PostAsync(issue.Key, Reported("first") with { EndedAt = Ended.AddHours(-3), Title = "earlier" });
        await h.PostAsync(issue.Key, Reported("second") with { EndedAt = Ended, Title = "later" });

        var log = await h.ReadAsync(issue.Key);

        // The question a work log gets asked is "what did this cost last night".
        Assert.Equal(["later", "earlier"], log.Entries.Select(e => e.Title));
        Assert.Equal(issue.Key, log.Key);
    }

    [Fact]
    public async Task EntriesAreTheIssuesOwnAndTotalsAreTheSubtrees()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the effort");
        var story = await h.FileAsync("story", "under it", parentKey: epic.Key);

        await h.PostAsync(epic.Key, Reported("planning") with { CostUsd = 1m });
        await h.PostAsync(story.Key, Reported("building") with { CostUsd = 4m });

        var log = await h.ReadAsync(epic.Key);

        // The asymmetry the page exists to say out loud, on the wire: one
        // session of its own, two beneath it.
        Assert.Single(log.Entries);
        Assert.Equal(1, log.Own.Sessions);
        Assert.Equal(2, log.Totals.Sessions);
        Assert.Equal(5m, log.Totals.CostUsd);
        Assert.Equal(1m, log.Own.CostUsd);
    }

    [Fact]
    public async Task AnIssueWithNoSessionsAnywhere_ReadsAsEmptyRatherThanFailing()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var log = await h.ReadAsync(issue.Key);

        Assert.Empty(log.Entries);
        Assert.Equal(0, log.Totals.Sessions);
        Assert.Equal(0, log.Totals.TotalTokens);
    }

    [Fact]
    public async Task ThePerModelBreakdown_SurvivesTheRoundTrip()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.PostAsync(issue.Key, Reported() with
        {
            Models =
            [
                new WorkLogModelUseDto("claude-opus-5", 900, 280, 3_200, 59_000, 0.0140m),
                new WorkLogModelUseDto("claude-haiku-4-5", 47, 1, 14, 57, 0.0006m),
            ],
        });

        var entry = (await h.ReadAsync(issue.Key)).Entries.Single();

        Assert.Equal(["claude-opus-5", "claude-haiku-4-5"], entry.Models.Select(m => m.Model));
        Assert.Equal(59_000, entry.Models[0].CacheReadTokens);
        Assert.Equal(entry.TotalTokens, entry.Models.Sum(m => m.InputTokens + m.OutputTokens + m.CacheCreationTokens + m.CacheReadTokens));
    }

    [Fact]
    public async Task ThePerson_MayReadTheLogEvenThoughOnlyAKeyMayWriteIt()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.PostAsync(issue.Key, Reported());

        h.Caller.Key = null;

        // Reading is the whole point of the page. It is writing that is closed.
        Assert.Single((await h.ReadAsync(issue.Key)).Entries);
    }

    [Fact]
    public async Task ReadingAnIssueThatDoesNotExist_IsNotFound()
    {
        var h = await NewAsync();

        Assert.IsType<NotFoundResult>((await h.WorkLog.GetWorkLog("AER-404", default)).Result);
        Assert.IsType<NotFoundResult>((await h.WorkLog.GetWorkLog("nonsense", default)).Result);
    }

    // ---- What it refuses ----

    [Fact]
    public async Task AnIssueThatDoesNotExist_IsNotFoundAndNothingIsWritten()
    {
        var h = await NewAsync();

        Assert.IsType<NotFoundResult>((await h.WorkLog.PostEntry("AER-404", Reported(), default)).Result);
        Assert.IsType<NotFoundResult>((await h.WorkLog.PostEntry("nonsense", Reported(), default)).Result);
        Assert.Empty(h.Db.WorkLog);
    }

    [Fact]
    public async Task ABrowserSession_CannotWriteAnEntryWhateverTheAdminGateSays()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        // A person, not a key. RequireAdmin would let this through - it accepts
        // both - and the check in the action is what makes the guarantee hold
        // where Auth:EnforceAdmin is off.
        h.Caller.Key = null;

        var refused = Assert.IsType<ObjectResult>((await h.WorkLog.PostEntry(issue.Key, Reported(), default)).Result);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.StatusCode);
        Assert.Empty(h.Db.WorkLog);
    }

    [Fact]
    public async Task ARowWithoutASession_IsRefused()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refused = Assert.IsType<BadRequestObjectResult>(
            (await h.WorkLog.PostEntry(issue.Key, Reported(" "), default)).Result);

        Assert.Equal("a work log entry needs the session it is about", refused.Value);
        Assert.Empty(h.Db.WorkLog);
    }

    [Fact]
    public void TheRoute_AcceptsTheHatchScope()
    {
        var guard = typeof(IssueWorkLogController)
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .Single();

        Assert.Equal(ApiKeyScopes.Hatch, guard.AcceptScope);
    }

    // ---- The trail it deliberately does not write ----

    [Fact]
    public async Task AnEntry_WritesNothingToTheAuditTrail()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var before = h.Db.IssueEvents.Count();

        await h.PostAsync(issue.Key, Reported());

        // The trail records what people and agents decided; a meter reading is
        // not a decision, and doubling it there would say nothing the row does
        // not.
        Assert.Equal(before, h.Db.IssueEvents.Count());
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 7, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 9, 7, 2, 40, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = new(2026, 9, 7, 2, 54, 0, TimeSpan.Zero);

    private const string Session = "3d1abf4f-0000-4000-8000-000000000001";

    /// <summary>A plain, successful, described session - the shape every test starts from.</summary>
    private static WorkLogEntryRequest Reported(string sessionId = Session) => new(
        sessionId, Started, Ended, 4_888, "did the thing", "and here is how", false, 3, 0.0146857m,
        [new WorkLogModelUseDto("claude-opus-5", 947, 281, 3_214, 59_057, 0.0146857m)]);

    private sealed class Harness
    {
        public required IssueWorkLogController WorkLog { get; init; }
        public required IssuesController Issues { get; init; }
        public required HatchContext Db { get; init; }
        public required StubCallerIdentity Caller { get; init; }
        public required int ProjectId { get; init; }

        public async Task<IssueDto> FileAsync(string type = "task", string title = "a thing", string? parentKey = null) =>
            Created(await Issues.CreateIssue(
                new IssueCreateRequest(ProjectId, type, title, null, parentKey, null, null), default));

        public async Task<WorkLogEntryDto> PostAsync(string key, WorkLogEntryRequest request) =>
            Value(await WorkLog.PostEntry(key, request, default));

        public async Task<WorkLogDto> ReadAsync(string key) => Value(await WorkLog.GetWorkLog(key, default));

        public async Task<IReadOnlyList<EfHatchWorkLogEntry>> RowsAsync(string key)
        {
            IssueKey.TryParse(key, out var projectKey, out var number);
            var issueId = await Db.Issues.AsNoTracking().WithKey(projectKey, number).Select(i => i.Id).FirstAsync();
            return await Db.WorkLog.AsNoTracking().Where(w => w.IssueId == issueId).ToListAsync();
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

        // The dispatcher, holding the operator's key. Every test that is not
        // about the refusal runs as one.
        var caller = new StubCallerIdentity
        {
            Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now },
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
            WorkLog = new IssueWorkLogController(db, time, caller),
            Issues = new IssuesController(db, new RankService(db), TestClaims.With(), caller, time),
            Db = db,
            Caller = caller,
            ProjectId = aerie.Id,
        };
    }

    /// <summary>Whoever the test says is holding the phone - see IssueDependenciesControllerTests.</summary>
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
