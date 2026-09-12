using System.Text.Json;
using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Modules.Hatch;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// Who owns a ticket: what the route accepts, what it refuses, what it writes
/// in the trail - and the property the whole design rests on, which is that an
/// API key cannot write one.
/// </summary>
public class AssigneeControllerTests
{
    // ---- Setting, changing, clearing ----

    [Fact]
    public async Task AnIssue_IsAssignedToAPerson()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");

        var assigned = await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);

        Assert.NotNull(assigned.Assignee);
        Assert.Equal(ActorKind.Person, assigned.Assignee.Kind);
        Assert.Equal(ada.Id, assigned.Assignee.Id);
        Assert.Equal("Ada", assigned.Assignee.Name);
    }

    [Fact]
    public async Task AnIssue_IsAssignedToAnApiKey()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var claude = h.Actors.AddKey("Claude in VS Code");

        var assigned = await h.AssignAsync(issue.Key, ActorKind.Key, claude.Id);

        Assert.NotNull(assigned.Assignee);
        Assert.Equal(ActorKind.Key, assigned.Assignee.Kind);
        Assert.Equal(claude.Id, assigned.Assignee.Id);
    }

    [Fact]
    public async Task AssigningAPerson_ClearsAKeyAlreadySet()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var claude = h.Actors.AddKey("Claude");
        var ada = h.Actors.AddPerson("Ada");

        await h.AssignAsync(issue.Key, ActorKind.Key, claude.Id);
        var assigned = await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);

        Assert.Equal(ActorKind.Person, assigned.Assignee!.Kind);

        // The columns, not just the projection: the check constraint is a
        // backstop and the in-memory provider does not enforce it, so this is
        // the only thing pinning the invariant.
        var row = await h.RowAsync(issue.Key);
        Assert.Equal(ada.Id, row.AssigneePersonId);
        Assert.Null(row.AssigneeApiKeyId);
    }

    [Fact]
    public async Task AssigningAKey_ClearsAPersonAlreadySet()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");
        var claude = h.Actors.AddKey("Claude");

        await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);
        await h.AssignAsync(issue.Key, ActorKind.Key, claude.Id);

        var row = await h.RowAsync(issue.Key);
        Assert.Null(row.AssigneePersonId);
        Assert.Equal(claude.Id, row.AssigneeApiKeyId);
    }

    [Fact]
    public async Task NeitherKindNorId_Unassigns()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");

        await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);
        var cleared = await h.UnassignAsync(issue.Key);

        Assert.Null(cleared.Assignee);

        var row = await h.RowAsync(issue.Key);
        Assert.Null(row.AssigneePersonId);
        Assert.Null(row.AssigneeApiKeyId);
    }

    [Fact]
    public async Task UnassigningWhatIsAlreadyNobodys_WritesNothing()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        h.Time.Advance(TimeSpan.FromHours(1));
        var cleared = await h.UnassignAsync(issue.Key);

        Assert.Null(cleared.Assignee);
        Assert.Equal(issue.UpdatedAt, cleared.UpdatedAt);
        Assert.Empty(await h.EventsAsync(issue.Key));
    }

    [Fact]
    public async Task ReassigningTheSamePerson_WritesNoEventAndDoesNotMoveUpdatedAt()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");

        var first = await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);

        h.Time.Advance(TimeSpan.FromHours(1));
        var again = await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);

        Assert.Equal(first.UpdatedAt, again.UpdatedAt);
        Assert.Single(await h.EventsAsync(issue.Key));
    }

    // ---- The trail ----

    [Fact]
    public async Task EachChange_WritesOneEventNamingBothSidesAndTheActor()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");
        var claude = h.Actors.AddKey("Claude");

        await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);
        await h.AssignAsync(issue.Key, ActorKind.Key, claude.Id);
        await h.UnassignAsync(issue.Key);

        var events = await h.EventsAsync(issue.Key);
        Assert.Equal(3, events.Count);

        // The caller's own name, not the assignee's: the trail says who did it,
        // and the payload says what they did.
        Assert.All(events, e => Assert.Equal("Nathan", e.Actor));

        var (from, to) = Sides(events[0]);
        Assert.Null(from);
        Assert.Equal((ActorKind.Person, ada.Id, "Ada"), to);

        (from, to) = Sides(events[1]);
        Assert.Equal((ActorKind.Person, ada.Id, "Ada"), from);
        Assert.Equal((ActorKind.Key, claude.Id, "Claude"), to);

        (from, to) = Sides(events[2]);
        Assert.Equal((ActorKind.Key, claude.Id, "Claude"), from);
        Assert.Null(to);
    }

    // ---- Refusals ----

    [Fact]
    public async Task AKindWithNoId_IsRefused()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refusal = await h.RefusedAsync(issue.Key, new AssigneeRequest(ActorKind.Person, null));

        Assert.Contains("a kind and an id, or neither", refusal);
    }

    [Fact]
    public async Task AnIdWithNoKind_IsRefused()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refusal = await h.RefusedAsync(issue.Key, new AssigneeRequest(null, Guid.NewGuid()));

        Assert.Contains("a kind and an id, or neither", refusal);
    }

    [Fact]
    public async Task AnUnknownKind_IsRefusedNamingTheTwo()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refusal = await h.RefusedAsync(issue.Key, new AssigneeRequest("robot", Guid.NewGuid()));

        Assert.Contains(ActorKind.Person, refusal);
        Assert.Contains(ActorKind.Key, refusal);
    }

    [Fact]
    public async Task APersonTheDirectoryDoesNotKnow_IsRefused()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refusal = await h.RefusedAsync(issue.Key, new AssigneeRequest(ActorKind.Person, Guid.NewGuid()));

        Assert.Equal("there is no such person", refusal);
    }

    [Fact]
    public async Task ARevokedKey_IsRefusedTheSameAsOneThatNeverExisted()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        // A revoked key is one the directory does not hand out - that is what
        // revocation means to every reader, and the write end reads it the same
        // way rather than with a rule of its own.
        var refusal = await h.RefusedAsync(issue.Key, new AssigneeRequest(ActorKind.Key, Guid.NewGuid()));

        Assert.Equal("there is no such key", refusal);
    }

    [Fact]
    public async Task ARefusedRequest_LeavesTheAssigneeAlone()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");

        await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);
        await h.RefusedAsync(issue.Key, new AssigneeRequest(ActorKind.Key, Guid.NewGuid()));

        var read = await h.ReadAsync(issue.Key);
        Assert.Equal(ada.Id, read.Assignee!.Id);
        Assert.Single(await h.EventsAsync(issue.Key));
    }

    [Fact]
    public async Task AnUnparseableKey_IsNotFound()
    {
        var h = await NewAsync();

        var result = await h.Assignee.PutIssueAssignee("not-a-key", new AssigneeRequest(null, null), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AKeyNamingNoIssue_IsNotFound()
    {
        var h = await NewAsync();

        var result = await h.Assignee.PutIssueAssignee("AER-4471", new AssigneeRequest(null, null), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- An identity that has stopped resolving ----

    [Fact]
    public async Task APersonSinceDeleted_ReadsAsUnassigned()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");

        await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);
        h.Actors.Live.Remove(ada);

        // No sweeper ran and the column still holds the id. The predicate is
        // what makes it read as nobody, and it cannot fail to run.
        Assert.Null((await h.ReadAsync(issue.Key)).Assignee);
        Assert.Equal(ada.Id, (await h.RowAsync(issue.Key)).AssigneePersonId);
    }

    [Fact]
    public async Task AKeySinceRevoked_ReadsAsUnassignedOnTheCardToo()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var claude = h.Actors.AddKey("Claude");

        await h.AssignAsync(issue.Key, ActorKind.Key, claude.Id);
        h.Actors.Live.Remove(claude);

        var board = Value(await h.Board.GetBoard(default));
        Assert.Null(Assert.Single(board.Issues, c => c.Key == issue.Key).Assignee);
    }

    // ---- The directory ----

    [Fact]
    public async Task TheDirectory_ListsWhoeverIsLive_AndSaysWhoIAm()
    {
        var h = await NewAsync();
        var ada = h.Actors.AddPerson("Ada");
        var claude = h.Actors.AddKey("Claude");
        h.Actors.Me = ada;

        var directory = Value(await h.Assignee.GetAssignees(default));

        Assert.Equal([ada.Id, claude.Id], directory.Assignees.Select(a => a.Id));
        Assert.Equal(ada.Id, directory.Me!.Id);
        Assert.Equal(ActorKind.Person, directory.Me.Kind);
    }

    [Fact]
    public async Task TheDirectory_SaysNobodyIsMe_WhenNobodyIsSignedIn()
    {
        var h = await NewAsync();
        h.Actors.AddPerson("Ada");

        // A browser with nothing behind it and no local caller either - the
        // page simply does not offer the press.
        var directory = Value(await h.Assignee.GetAssignees(default));

        Assert.Null(directory.Me);
        Assert.Single(directory.Assignees);
    }

    [Fact]
    public async Task TheDirectory_DoesNotOfferMeAnIdentityItNoLongerKnows()
    {
        var h = await NewAsync();

        // Signed in as somebody the directory has stopped listing: a press that
        // would only be refused is not offered.
        h.Actors.Me = new Actor(ActorKind.Person, Guid.NewGuid(), "Ada");

        Assert.Null(Value(await h.Assignee.GetAssignees(default)).Me);
    }

    // ---- Local mode ----

    /// <summary>
    /// With the wall off the local person leads the directory and is the press
    /// "Assign to me" draws. How they get into the list is
    /// ActorDirectoryTests; this is that the controller holds no separate
    /// opinion about somebody who is not a row in People.
    /// </summary>
    [Fact]
    public async Task TheDirectory_OffersTheLocalPersonAsMe()
    {
        var h = await NewAsync();
        h.Actors.Me = h.Actors.AddPerson("Ada", LocalCaller.PersonId);

        var directory = Value(await h.Assignee.GetAssignees(default));

        Assert.Equal(LocalCaller.PersonId, directory.Me!.Id);
        Assert.Contains(directory.Assignees, a => a.Id == LocalCaller.PersonId);
    }

    [Fact]
    public async Task AnIssue_IsAssignedToTheLocalPerson()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        h.Actors.AddPerson("Ada", LocalCaller.PersonId);

        var assigned = await h.AssignAsync(issue.Key, ActorKind.Person, LocalCaller.PersonId);

        Assert.Equal(LocalCaller.PersonId, assigned.Assignee!.Id);
        Assert.Equal("Ada", assigned.Assignee.Name);

        // The id alone goes in the column and the name is drawn on the way out,
        // which is what makes renaming yourself cost nothing.
        Assert.Equal(LocalCaller.PersonId, (await h.RowAsync(issue.Key)).AssigneePersonId);
    }

    /// <summary>
    /// A runner is who is calling and is still not in the directory, so the
    /// lookup inside finds nothing - the correct offer to make to something
    /// nothing may be assigned to.
    /// </summary>
    [Fact]
    public async Task ARunner_IsOfferedNoPress()
    {
        var h = await NewAsync();
        h.Actors.AddPerson("Ada", LocalCaller.PersonId);
        h.Actors.Me = new Actor(ActorKind.Key, LocalCaller.RunnerIdFor("host:/src"), "host:/src");

        Assert.Null(Value(await h.Assignee.GetAssignees(default)).Me);
    }

    // ---- The one property the design rests on ----

    [Fact]
    public void TheWrite_NamesNoScope_SoAKeyIsRefused()
    {
        var guard = typeof(AssigneeController)
            .GetMethod(nameof(AssigneeController.PutIssueAssignee))!
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .SingleOrDefault();

        // No scope named, and no class-level attribute to inherit one from:
        // under "people only" an assignee is a dispatch gate, and a key that
        // could write one could hand itself work somebody had reserved.
        Assert.NotNull(guard);
        Assert.Null(guard.AcceptScope);

        Assert.Empty(typeof(AssigneeController)
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false));
    }

    [Fact]
    public void TheDirectoryRead_TakesTheHatchScope()
    {
        var guard = typeof(AssigneeController)
            .GetMethod(nameof(AssigneeController.GetAssignees))!
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .Single();

        // Reading stays open, like everything else a dispatch needs: an agent
        // has to be able to say whose ticket it is leaving alone.
        Assert.Equal(ApiKeyScopes.Hatch, guard.AcceptScope);
    }

    [Fact]
    public async Task ReadingAnAssignee_IsOpenToAKey()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        var ada = h.Actors.AddPerson("Ada");
        await h.AssignAsync(issue.Key, ActorKind.Person, ada.Id);

        // It rides IssueDto, which IssuesController hands out to the hatch
        // scope - the same lane the model override reads through.
        Assert.Equal("Ada", (await h.ReadAsync(issue.Key)).Assignee!.Name);
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required StubActorDirectory Actors { get; init; }
        public required AssigneeController Assignee { get; init; }
        public required IssuesController Issues { get; init; }
        public required BoardController Board { get; init; }
        public required IssueThreadController Thread { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required int ProjectId { get; init; }

        public async Task<IssueDto> FileAsync(string type = "task", string title = "a thing") =>
            Created(await Issues.CreateIssue(new IssueCreateRequest(ProjectId, type, title, null, null, null, null), default));

        public async Task<IssueDto> AssignAsync(string key, string kind, Guid id) =>
            Value(await Assignee.PutIssueAssignee(key, new AssigneeRequest(kind, id), default));

        public async Task<IssueDto> UnassignAsync(string key) =>
            Value(await Assignee.PutIssueAssignee(key, new AssigneeRequest(null, null), default));

        public async Task<IssueDto> ReadAsync(string key) => Value(await Issues.GetIssue(key, default));

        /// <summary>The sentence a refused request comes back with.</summary>
        public async Task<string> RefusedAsync(string key, AssigneeRequest request) =>
            Reason((await Assignee.PutIssueAssignee(key, request, default)).Result);

        /// <summary>The columns themselves, which is where the exclusivity invariant lives.</summary>
        public async Task<EfHatchIssue> RowAsync(string key)
        {
            IssueKey.TryParse(key, out var projectKey, out var number);
            return await Db.Issues.AsNoTracking().WithKey(projectKey, number).FirstAsync();
        }

        /// <summary>The assignee's own events, oldest first.</summary>
        public async Task<IReadOnlyList<IssueEventDto>> EventsAsync(string key) =>
            Value(await Thread.GetEvents(key, default))
                .Where(e => e.Kind == EfHatchIssueEvent.AssigneeChanged)
                .Reverse()
                .ToList();
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
        var caller = new StubCallerIdentity { Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now } };
        var actors = new StubActorDirectory();

        return new Harness
        {
            Db = db,
            Actors = actors,
            Assignee = new AssigneeController(db, actors, TestClaims.With(), caller, time),
            Issues = new IssuesController(db, new RankService(db), actors, TestClaims.With(), caller, time),
            Board = new BoardController(db, actors, TestClaims.With(), time),
            Thread = new IssueThreadController(db, caller, time),
            Time = time,
            ProjectId = hatch.Id,
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

    /// <summary>The two sides an assignee_changed event carries, each whole or null.</summary>
    private static ((string Kind, Guid Id, string Name)? From, (string Kind, Guid Id, string Name)? To) Sides(
        IssueEventDto e)
    {
        var payload = e.Payload!.Value;
        return (Side(payload.GetProperty("from")), Side(payload.GetProperty("to")));
    }

    private static (string Kind, Guid Id, string Name)? Side(JsonElement side) =>
        side.ValueKind == JsonValueKind.Null
            ? null
            : (side.GetProperty("kind").GetString()!,
               side.GetProperty("id").GetGuid(),
               side.GetProperty("name").GetString()!);

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
