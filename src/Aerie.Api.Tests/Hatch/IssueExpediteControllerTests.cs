using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// <em>This one first.</em> What the route writes, what it refuses to write
/// twice, what nothing else is allowed to clear - and the property the whole
/// design rests on, which is that an API key cannot set one.
/// </summary>
public class IssueExpediteControllerTests
{
    // ---- Marking, and unmarking ----

    [Fact]
    public async Task AnIssue_IsNotExpeditedUntilSomebodySaysSo()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        Assert.False(issue.Expedited);
    }

    [Fact]
    public async Task AnIssue_IsMarkedExpedited()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var marked = await h.ExpediteAsync(issue.Key, true);

        Assert.True(marked.Expedited);
        Assert.True((await h.RowAsync(issue.Key)).Expedited);
    }

    [Fact]
    public async Task TheSameControl_UnmarksIt()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.ExpediteAsync(issue.Key, true);
        var unmarked = await h.ExpediteAsync(issue.Key, false);

        Assert.False(unmarked.Expedited);
        Assert.False((await h.RowAsync(issue.Key)).Expedited);
    }

    [Fact]
    public async Task AnIssueThatIsNotThere_IsNotFound()
    {
        var h = await NewAsync();

        Assert.IsType<NotFoundResult>(
            (await h.Expedite.PutIssueExpedite("AER-404", new ExpediteRequest(true), default)).Result);
    }

    // ---- The trail ----

    [Fact]
    public async Task Marking_WritesWhoDidItAndWhichWay()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.ExpediteAsync(issue.Key, true);

        var e = Assert.Single(await h.EventsAsync(issue.Key));
        Assert.Equal("Nathan", e.Actor);
        Assert.Equal((false, true), Sides(e));
    }

    [Fact]
    public async Task Unmarking_WritesItsOwnRowTheOtherWayRound()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.ExpediteAsync(issue.Key, true);
        await h.ExpediteAsync(issue.Key, false);

        var events = await h.EventsAsync(issue.Key);
        Assert.Equal(2, events.Count);
        Assert.Equal((false, true), Sides(events[0]));
        Assert.Equal((true, false), Sides(events[1]));
    }

    // ---- Setting what it already holds ----

    [Fact]
    public async Task SettingTheValueItAlreadyHolds_WritesNothing()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ExpediteAsync(issue.Key, true);

        var again = await h.ExpediteAsync(issue.Key, true);

        Assert.True(again.Expedited);
        Assert.Single(await h.EventsAsync(issue.Key));
    }

    [Fact]
    public async Task SettingTheValueItAlreadyHolds_DoesNotMoveTheUpdatedTime()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ExpediteAsync(issue.Key, true);

        var was = (await h.ReadAsync(issue.Key)).UpdatedAt;
        h.Time.Advance(TimeSpan.FromHours(1));
        await h.ExpediteAsync(issue.Key, true);

        Assert.Equal(was, (await h.ReadAsync(issue.Key)).UpdatedAt);
    }

    [Fact]
    public async Task UnmarkingSomethingNeverMarked_WritesNothing()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var unmarked = await h.ExpediteAsync(issue.Key, false);

        Assert.False(unmarked.Expedited);
        Assert.Empty(await h.EventsAsync(issue.Key));
    }

    // ---- Nothing else clears it ----

    [Fact]
    public async Task Moving_RetitlingAndReparenting_LeaveItAsItWasSet()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the epic");
        var issue = await h.FileAsync("story", "the story");
        await h.ExpediteAsync(issue.Key, true);

        // Every other way an issue is written, one after another. The flag is
        // written by one route and read by everything, so "nothing clears it"
        // is a claim about every other route rather than about this one.
        await h.PatchAsync(issue.Key, new IssuePatchRequest(
            Title: "renamed", Description: null, Type: null, StatusId: null, ParentKey: epic.Key,
            ReadyAt: null, DueAt: null, PullRequestUrl: null));

        var moved = Value(await h.Issues.MoveIssue(
            issue.Key, new IssueMoveRequest(h.DoneId, null, null), default));

        Assert.True(moved.Expedited);
        Assert.True((await h.RowAsync(issue.Key)).Expedited);
    }

    // ---- The edge that is cut ----

    [Fact]
    public void SettingIt_IsClosedToAnApiKey()
    {
        var guard = typeof(IssueExpediteController)
            .GetMethod(nameof(IssueExpediteController.PutIssueExpedite))!
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .SingleOrDefault();

        // No scope named, and no class-level attribute to inherit one from:
        // expedite decides what the loop reaches for first, so a key that could
        // set one could put its own ticket at the front of every night.
        Assert.NotNull(guard);
        Assert.Null(guard.AcceptScope);

        Assert.Empty(typeof(IssueExpediteController)
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false));
    }

    [Fact]
    public async Task ReadingIt_IsOpenToAKey()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ExpediteAsync(issue.Key, true);

        // It rides IssueDto and IssueCardDto, both of which IssuesController
        // hands out to the hatch scope: an agent is entitled to know why it was
        // sent where it was sent.
        Assert.True((await h.ReadAsync(issue.Key)).Expedited);

        var cards = Value(await h.Issues.SearchIssues(null, null, null, null, null, null, default));
        Assert.True(cards.Single(c => c.Key == issue.Key).Expedited);
    }

    [Fact]
    public async Task TheFlagRidesTheBoardCard()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ExpediteAsync(issue.Key, true);

        var board = Value(await h.Board.GetBoard(default));

        Assert.True(board.Issues.Single(c => c.Key == issue.Key).Expedited);
    }

    // ---- The float ----

    [Fact]
    public async Task AnExpeditedCard_IsServedAboveEveryOtherCardInItsColumn()
    {
        var h = await NewAsync();
        await h.FileAsync(title: "first");
        await h.FileAsync(title: "second");
        var last = await h.FileAsync(title: "last");

        await h.ExpediteAsync(last.Key, true);

        // The bottom card of the column, above the two that outrank it. The
        // ordering is the server's, so this is what a client that only slices
        // the list will draw.
        Assert.Equal([last.Key, "AER-1", "AER-2"], await h.ColumnAsync(h.InboxId));
    }

    [Fact]
    public async Task TwoExpeditedCards_KeepTheBoardsOwnOrderBetweenThem()
    {
        var h = await NewAsync();
        var first = await h.FileAsync(title: "first");
        await h.FileAsync(title: "second");
        var last = await h.FileAsync(title: "last");

        await h.ExpediteAsync(last.Key, true);
        await h.ExpediteAsync(first.Key, true);

        // (Rank, Id) inside the expedited half as well as outside it - the
        // float is one more key in front of the tuple, not a replacement for
        // it.
        Assert.Equal([first.Key, last.Key, "AER-2"], await h.ColumnAsync(h.InboxId));
    }

    [Fact]
    public async Task Unmarking_ReturnsTheCardToItsRankPosition()
    {
        var h = await NewAsync();
        await h.FileAsync(title: "first");
        await h.FileAsync(title: "second");
        var last = await h.FileAsync(title: "last");

        await h.ExpediteAsync(last.Key, true);
        await h.ExpediteAsync(last.Key, false);

        Assert.Equal(["AER-1", "AER-2", last.Key], await h.ColumnAsync(h.InboxId));
    }

    [Fact]
    public async Task TheFloat_IsWithinAColumnAndNotAcrossTheBoard()
    {
        var h = await NewAsync();
        var here = await h.FileAsync(title: "here");
        var there = await h.FileAsync(title: "there");
        Value(await h.Issues.MoveIssue(there.Key, new IssueMoveRequest(h.DoneId, null, null), default));

        await h.ExpediteAsync(there.Key, true);

        // Expedite is a sort key inside a column. Where the columns themselves
        // sit is the status's SortOrder, and nothing about a card moves that.
        Assert.Equal([here.Key], await h.ColumnAsync(h.InboxId));
        Assert.Equal([there.Key], await h.ColumnAsync(h.DoneId));
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required IssueExpediteController Expedite { get; init; }
        public required IssuesController Issues { get; init; }
        public required BoardController Board { get; init; }
        public required IssueThreadController Thread { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required int ProjectId { get; init; }
        public required int InboxId { get; init; }
        public required int DoneId { get; init; }

        public async Task<IssueDto> FileAsync(string type = "task", string title = "a thing") =>
            Created(await Issues.CreateIssue(new IssueCreateRequest(ProjectId, type, title, null, null, null, null), default));

        public async Task<IssueDto> ExpediteAsync(string key, bool expedited) =>
            Value(await Expedite.PutIssueExpedite(key, new ExpediteRequest(expedited), default));

        public async Task<IssueDto> ReadAsync(string key) => Value(await Issues.GetIssue(key, default));

        public async Task<IssueDto> PatchAsync(string key, IssuePatchRequest patch) =>
            Value(await Issues.PatchIssue(key, patch, default));

        /// <summary>One column of the board, in the order the server served it.</summary>
        public async Task<IReadOnlyList<string>> ColumnAsync(int statusId) =>
            Value(await Board.GetBoard(default)).Issues
                .Where(c => c.StatusId == statusId)
                .Select(c => c.Key)
                .ToList();

        /// <summary>The column itself, which is what the projection is read against.</summary>
        public async Task<EfHatchIssue> RowAsync(string key)
        {
            IssueKey.TryParse(key, out var projectKey, out var number);
            return await Db.Issues.AsNoTracking().WithKey(projectKey, number).FirstAsync();
        }

        /// <summary>The flag's own events, oldest first.</summary>
        public async Task<IReadOnlyList<IssueEventDto>> EventsAsync(string key) =>
            Value(await Thread.GetEvents(key, default))
                .Where(e => e.Kind == EfHatchIssueEvent.ExpeditedChanged)
                .Reverse()
                .ToList();
    }

    private static async Task<Harness> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var aerie = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        db.Add(aerie);
        var inbox = new EfHatchStatus { Name = "inbox", SortOrder = 10 };
        db.Add(inbox);
        var done = new EfHatchStatus { Name = "done", SortOrder = 20, IsTerminal = true };
        db.Add(done);
        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(Now);
        var caller = new StubCallerIdentity { Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now } };
        var actors = new StubActorDirectory();

        return new Harness
        {
            Db = db,
            Expedite = new IssueExpediteController(db, actors, TestClaims.With(), caller, time),
            Issues = new IssuesController(db, new RankService(db), actors, TestClaims.With(), caller, time),
            Board = new BoardController(db, actors, TestClaims.With(), time),
            Thread = new IssueThreadController(db, caller, time),
            Time = time,
            ProjectId = aerie.Id,
            InboxId = inbox.Id,
            DoneId = done.Id,
        };
    }

    /// <summary>Whoever the test says is holding the phone. Their name is the audit actor.</summary>
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

    /// <summary>The two sides an <c>expedited_changed</c> event carries.</summary>
    private static (bool From, bool To) Sides(IssueEventDto e)
    {
        var payload = e.Payload!.Value;
        return (payload.GetProperty("from").GetBoolean(), payload.GetProperty("to").GetBoolean());
    }

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {Reason(result.Result)}");

    private static T Created<T>(ActionResult<T> result) =>
        result.Result is CreatedAtActionResult created
            ? (T)created.Value!
            : result.Value ?? throw new InvalidOperationException($"expected a created value, got {Reason(result.Result)}");

    /// <summary>The plain-text reason on a refusal - what the UI puts on screen.</summary>
    private static string Reason(IActionResult? result) => result switch
    {
        ObjectResult o => o.Value?.ToString() ?? $"{o.StatusCode}",
        StatusCodeResult s => s.StatusCode.ToString(),
        null => "no result",
        _ => result.GetType().Name,
    };
}
