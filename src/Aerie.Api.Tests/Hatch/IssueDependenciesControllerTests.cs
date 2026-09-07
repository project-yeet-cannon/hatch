using System.Reflection;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The two verbs that say one issue waits on another: what they accept, what
/// they refuse, what they write in the trail - and the property the whole
/// feature rests on, which is that an API key may use both.
/// </summary>
public class IssueDependenciesControllerTests
{
    // ---- Adding and removing ----

    [Fact]
    public async Task AnEdge_IsAddedAndReadBackOnBothIssues()
    {
        var h = await NewAsync();
        var first = await h.FileAsync(title: "phase one");
        var second = await h.FileAsync(title: "phase two");

        var waiting = await h.AddAsync(second.Key, first.Key);

        Assert.Equal([first.Key], waiting.DependsOnKeys);
        Assert.Empty(waiting.DependentKeys);

        // The same edge read from the other end, which is where the Blocks
        // list on the blocker's own page comes from.
        var blocker = await h.ReadAsync(first.Key);
        Assert.Equal([second.Key], blocker.DependentKeys);
        Assert.Empty(blocker.DependsOnKeys);
    }

    [Fact]
    public async Task AnIssue_MayWaitOnAnyNumberOfThings()
    {
        var h = await NewAsync();
        var one = await h.FileAsync(title: "one");
        var two = await h.FileAsync(title: "two");
        var last = await h.FileAsync(title: "last");

        await h.AddAsync(last.Key, two.Key);
        var waiting = await h.AddAsync(last.Key, one.Key);

        // Key order rather than the order they were filed in, so the card and
        // the fold's sentence read the same way twice running.
        Assert.Equal([one.Key, two.Key], waiting.DependsOnKeys);
    }

    [Fact]
    public async Task AddingAnEdgeTwice_WritesOneRowAndOneEvent()
    {
        var h = await NewAsync();
        var first = await h.FileAsync();
        var second = await h.FileAsync();

        await h.AddAsync(second.Key, first.Key);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        var again = await h.AddAsync(second.Key, first.Key);

        // Re-applying an edit is safe everywhere else in Hatch and is safe
        // here: no second row, no second event, and UpdatedAt where it was.
        Assert.Equal([first.Key], again.DependsOnKeys);
        Assert.Single(await h.EventsAsync(second.Key));
        Assert.Equal(Now, again.UpdatedAt);
    }

    [Fact]
    public async Task AnEdge_IsRemoved()
    {
        var h = await NewAsync();
        var first = await h.FileAsync();
        var second = await h.FileAsync();
        await h.AddAsync(second.Key, first.Key);

        var freed = await h.RemoveAsync(second.Key, first.Key);

        Assert.Empty(freed.DependsOnKeys);
        Assert.Empty((await h.ReadAsync(first.Key)).DependentKeys);
    }

    [Fact]
    public async Task RemovingAnEdgeThatIsNotThere_WritesNothing()
    {
        var h = await NewAsync();
        var first = await h.FileAsync();
        var second = await h.FileAsync();

        h.Time.Advance(TimeSpan.FromMinutes(5));
        var unchanged = await h.RemoveAsync(second.Key, first.Key);

        // A DELETE says what should not exist afterwards, and it does not.
        Assert.Empty(unchanged.DependsOnKeys);
        Assert.Empty(await h.EventsAsync(second.Key));
        Assert.Equal(Now, unchanged.UpdatedAt);
    }

    [Fact]
    public async Task AnEdge_MayCrossAProject()
    {
        var h = await NewAsync();
        var here = await h.FileAsync(title: "ours");
        var there = await h.FileAsync(projectId: h.OtherProjectId, title: "theirs");

        // A parent may not cross a project and this may: containment and
        // ordering are different claims, and two efforts routinely have to
        // land in order.
        Assert.Equal([there.Key], (await h.AddAsync(here.Key, there.Key)).DependsOnKeys);
    }

    // ---- What it refuses ----

    [Fact]
    public async Task AnUnknownIssue_IsNotFound()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        Assert.IsType<NotFoundResult>((await h.Dependencies.AddDependency("AER-404", new IssueDependencyRequest(issue.Key), default)).Result);
        Assert.IsType<NotFoundResult>((await h.Dependencies.RemoveDependency("AER-404", issue.Key, default)).Result);
    }

    [Fact]
    public async Task AKeyThatIsNotOne_IsRefusedInTheSentenceTheParentPathUses()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        Assert.Equal("\"nonsense\" is not an issue key", await h.RefusedAsync(issue.Key, "nonsense"));
        await h.AssertNothingWrittenAsync(issue.Key);
    }

    [Fact]
    public async Task AKeyNobodyMinted_IsRefused()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        Assert.Equal("there is no AER-404", await h.RefusedAsync(issue.Key, "AER-404"));
        await h.AssertNothingWrittenAsync(issue.Key);
    }

    [Fact]
    public async Task AnIssue_CannotDependOnItself()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        Assert.Equal("an issue cannot depend on itself", await h.RefusedAsync(issue.Key, issue.Key));
        await h.AssertNothingWrittenAsync(issue.Key);
    }

    [Fact]
    public async Task AnIssue_CannotDependOnItsAncestor()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the effort");
        var story = await h.FileAsync("story", "under it", parentKey: epic.Key);
        var task = await h.FileAsync("task", "under that", parentKey: story.Key);

        // A parent is not done until its work is, so an edge upwards is a
        // deadlock with a nicer name - at any depth.
        Assert.Equal(
            $"{epic.Key} is above this issue - a dependency between them could never be satisfied",
            await h.RefusedAsync(task.Key, epic.Key));

        await h.AssertNothingWrittenAsync(task.Key);
    }

    [Fact]
    public async Task AnIssue_CannotDependOnItsDescendant()
    {
        var h = await NewAsync();
        var epic = await h.FileAsync("epic", "the effort");
        var story = await h.FileAsync("story", "under it", parentKey: epic.Key);
        var task = await h.FileAsync("task", "under that", parentKey: story.Key);

        Assert.Equal(
            $"{task.Key} is below this issue - a dependency between them could never be satisfied",
            await h.RefusedAsync(epic.Key, task.Key));

        await h.AssertNothingWrittenAsync(epic.Key);
    }

    [Fact]
    public async Task AnIssue_CannotDependOnSomethingAlreadyWaitingOnIt()
    {
        var h = await NewAsync();
        var one = await h.FileAsync(title: "one");
        var two = await h.FileAsync(title: "two");
        var three = await h.FileAsync(title: "three");

        await h.AddAsync(two.Key, one.Key);
        await h.AddAsync(three.Key, two.Key);

        // Through a chain and not merely directly: a linked list closed into a
        // ring is a subtree the loop would never dispatch anything from.
        Assert.Equal(
            $"{three.Key} is already waiting on this issue",
            await h.RefusedAsync(one.Key, three.Key));

        await h.AssertNothingWrittenAsync(one.Key);
    }

    // ---- The trail ----

    [Fact]
    public async Task AddingAndRemoving_EachLandInTheHistory()
    {
        var h = await NewAsync();
        var first = await h.FileAsync();
        var second = await h.FileAsync();

        await h.AddAsync(second.Key, first.Key);
        await h.RemoveAsync(second.Key, first.Key);

        var events = await h.EventsAsync(second.Key);

        Assert.Equal(
            [EfHatchIssueEvent.DependencyAdded, EfHatchIssueEvent.DependencyRemoved],
            events.Select(e => e.Kind));

        // ParentChanged's shape, so the issue page's event line reads it with
        // no new case: null on the side the edge was not on.
        Assert.Equal((null, first.Key), Payload(events[0]));
        Assert.Equal((first.Key, null), Payload(events[1]));

        Assert.All(events, e => Assert.Equal("Nathan", e.Actor));
    }

    [Fact]
    public async Task TheEvent_IsWrittenOnTheIssueThatWaitsAndNotOnTheBlocker()
    {
        var h = await NewAsync();
        var first = await h.FileAsync();
        var second = await h.FileAsync();

        await h.AddAsync(second.Key, first.Key);

        // The edge is the waiting issue's. A second event on the blocker would
        // be the same fact filed twice.
        Assert.Single(await h.EventsAsync(second.Key));
        Assert.Empty(await h.EventsAsync(first.Key));
    }

    // ---- The edge that is deliberately *not* cut ----

    [Fact]
    public void BothVerbs_AreOpenToAnApiKey()
    {
        var guard = typeof(IssueDependenciesController)
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .Single();

        // Unlike a playbook and unlike the per-issue override, which name no
        // scope: an edge is a statement about the work rather than about an
        // agent's budget, and a planning session that has just filed five
        // stories is exactly who should chain them.
        Assert.Equal(ApiKeyScopes.Hatch, guard.AcceptScope);

        Assert.All(
            new[] { nameof(IssueDependenciesController.AddDependency), nameof(IssueDependenciesController.RemoveDependency) },
            name => Assert.Empty(
                typeof(IssueDependenciesController).GetMethod(name)!
                    .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)));
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required IssueDependenciesController Dependencies { get; init; }
        public required IssuesController Issues { get; init; }
        public required IssueThreadController Thread { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required int ProjectId { get; init; }
        public required int OtherProjectId { get; init; }

        public async Task<IssueDto> FileAsync(
            string type = "task", string title = "a thing", string? parentKey = null, int? projectId = null) =>
            Created(await Issues.CreateIssue(
                new IssueCreateRequest(projectId ?? ProjectId, type, title, null, parentKey, null, null), default));

        public async Task<IssueDto> AddAsync(string key, string dependsOnKey) =>
            Value(await Dependencies.AddDependency(key, new IssueDependencyRequest(dependsOnKey), default));

        public async Task<IssueDto> RemoveAsync(string key, string dependsOnKey) =>
            Value(await Dependencies.RemoveDependency(key, dependsOnKey, default));

        /// <summary>The sentence a refused add came back with.</summary>
        public async Task<string?> RefusedAsync(string key, string dependsOnKey) =>
            Assert.IsType<BadRequestObjectResult>(
                (await Dependencies.AddDependency(key, new IssueDependencyRequest(dependsOnKey), default)).Result)
                .Value?.ToString();

        /// <summary>
        /// A refusal leaves the table exactly as it was, and the trail with it:
        /// the issue waits on nothing it did not already wait on, and nothing
        /// was logged.
        /// </summary>
        public async Task AssertNothingWrittenAsync(string key, params string[] alreadyWaitingOn)
        {
            Assert.Equal(alreadyWaitingOn, (await ReadAsync(key)).DependsOnKeys);
            Assert.Empty(await EventsAsync(key));
        }

        public async Task<IssueDto> ReadAsync(string key) => Value(await Issues.GetIssue(key, default));

        /// <summary>The dependency events on an issue, oldest first.</summary>
        public async Task<IReadOnlyList<IssueEventDto>> EventsAsync(string key) =>
            Value(await Thread.GetEvents(key, default))
                .Where(e => e.Kind is EfHatchIssueEvent.DependencyAdded or EfHatchIssueEvent.DependencyRemoved)
                .Reverse()
                .ToList();
    }

    private static async Task<Harness> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var aerie = new EfHatchProject { Key = "AER", Name = "Aerie", CreatedAt = Now };
        var other = new EfHatchProject { Key = "OPS", Name = "Operations", CreatedAt = Now };
        db.AddRange(aerie, other);
        db.Add(new EfHatchStatus { Name = "inbox", SortOrder = 10 });
        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(Now);
        var caller = new StubCallerIdentity { Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now } };

        return new Harness
        {
            Dependencies = new IssueDependenciesController(db, new StubActorDirectory(), caller, time),
            Issues = new IssuesController(db, new RankService(db), new StubActorDirectory(), caller, time),
            Thread = new IssueThreadController(db, caller, time),
            Time = time,
            ProjectId = aerie.Id,
            OtherProjectId = other.Id,
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

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(Person?.Name ?? Key?.Name ?? CallerIdentity.Unattributed);
    }

    /// <summary>The <c>from</c> and <c>to</c> an event carries.</summary>
    private static (string? From, string? To) Payload(IssueEventDto e)
    {
        var payload = e.Payload!.Value;
        return (payload.GetProperty("from").GetString(), payload.GetProperty("to").GetString());
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
