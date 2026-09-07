using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The per-issue model and effort override: what it accepts, what it refuses,
/// what it writes in the trail - and the one property the whole design rests
/// on, which is that an API key cannot set one.
/// </summary>
public class IssuePlaybookControllerTests
{
    // ---- Setting, changing, clearing ----

    [Fact]
    public async Task AModel_IsSetAndReadBack()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var patched = Value(await h.Playbook.PatchIssuePlaybook(issue.Key, new IssuePlaybookRequest("opus", null), default));

        Assert.Equal("opus", patched.ModelOverride);
        Assert.Null(patched.EffortOverride);
    }

    [Fact]
    public async Task AnEffort_IsSetOnItsOwn()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var patched = Value(await h.Playbook.PatchIssuePlaybook(issue.Key, new IssuePlaybookRequest(null, "xhigh"), default));

        // The two are independent: an issue may carry an effort and no model.
        Assert.Null(patched.ModelOverride);
        Assert.Equal("xhigh", patched.EffortOverride);
    }

    [Fact]
    public async Task ANullField_LeavesTheOtherAlone()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SetAsync(issue.Key, "opus", "max");
        var patched = await h.SetAsync(issue.Key, "haiku", null);

        Assert.Equal("haiku", patched.ModelOverride);
        Assert.Equal("max", patched.EffortOverride);
    }

    [Fact]
    public async Task AnEmptyString_ClearsTheOverride()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SetAsync(issue.Key, "opus", "max");
        var patched = await h.SetAsync(issue.Key, "", null);

        Assert.Null(patched.ModelOverride);
        Assert.Equal("max", patched.EffortOverride);
    }

    [Fact]
    public async Task Whitespace_ClearsIt_TheSameAsEmpty()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SetAsync(issue.Key, "opus", "max");
        var patched = await h.SetAsync(issue.Key, null, "   ");

        Assert.Equal("opus", patched.ModelOverride);
        Assert.Null(patched.EffortOverride);
    }

    [Fact]
    public async Task AValue_IsTrimmed()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var patched = await h.SetAsync(issue.Key, "  sonnet  ", null);

        Assert.Equal("sonnet", patched.ModelOverride);
    }

    [Fact]
    public async Task APinnedModelId_IsAccepted()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        // Exactly what a playbook accepts, because it is the same rule: the
        // four aliases or a pinned name. The picker not offering this is a
        // statement about the picker.
        var patched = await h.SetAsync(issue.Key, "claude-opus-5", null);

        Assert.Equal("claude-opus-5", patched.ModelOverride);
    }

    // ---- Refusals ----

    [Fact]
    public async Task ABadModel_IsRefusedInThePlaybooksPagesOwnWords()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refusal = await h.Playbook.PatchIssuePlaybook(issue.Key, new IssuePlaybookRequest("gpt-4", null), default);

        Assert.Equal(
            "a model is one of haiku, sonnet, opus, fable, or a pinned name like claude-opus-5 - not \"gpt-4\"",
            Reason(refusal.Result));
    }

    [Fact]
    public async Task ABadEffort_IsRefusedTheSameWay()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refusal = await h.Playbook.PatchIssuePlaybook(issue.Key, new IssuePlaybookRequest(null, "ludicrous"), default);

        Assert.Equal(
            "an effort is one of low, medium, high, xhigh, max - not \"ludicrous\"",
            Reason(refusal.Result));
    }

    [Fact]
    public async Task ABadFieldBesideAGoodOne_WritesNeither()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        var refusal = await h.Playbook.PatchIssuePlaybook(issue.Key, new IssuePlaybookRequest("gpt-4", "max"), default);
        Assert.IsType<BadRequestObjectResult>(refusal.Result);

        // The order inside the verb is the guarantee: everything present is
        // validated before the entity is touched.
        var after = await h.ReadAsync(issue.Key);
        Assert.Null(after.ModelOverride);
        Assert.Null(after.EffortOverride);
        Assert.Empty(await h.EventsAsync(issue.Key));
    }

    [Fact]
    public async Task AnUnknownKey_Is404()
    {
        var h = await NewAsync();

        Assert.IsType<NotFoundResult>(
            (await h.Playbook.PatchIssuePlaybook("AER-404", new IssuePlaybookRequest("opus", null), default)).Result);

        Assert.IsType<NotFoundResult>(
            (await h.Playbook.PatchIssuePlaybook("not a key", new IssuePlaybookRequest("opus", null), default)).Result);
    }

    // ---- The trail ----

    [Fact]
    public async Task SettingAndClearing_EachNameWhatTheValueWentFromAndTo()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SetAsync(issue.Key, "opus", "max");
        await h.SetAsync(issue.Key, "sonnet", null);
        await h.SetAsync(issue.Key, "", null);

        var events = (await h.EventsAsync(issue.Key)).ToList();

        Assert.Equal(
            [
                "model_override_changed",
                "effort_override_changed",
                "model_override_changed",
                "model_override_changed",
            ],
            events.Select(e => e.Kind));

        Assert.All(events, e => Assert.Equal("Nathan", e.Actor));

        var models = events.Where(e => e.Kind == EfHatchIssueEvent.ModelOverrideChanged).ToList();
        Assert.Equal((null, "opus"), Payload(models[0]));
        Assert.Equal(("opus", "sonnet"), Payload(models[1]));
        Assert.Equal(("sonnet", null), Payload(models[2]));

        Assert.Equal((null, "max"), Payload(events.Single(e => e.Kind == EfHatchIssueEvent.EffortOverrideChanged)));
    }

    [Fact]
    public async Task SettingAFieldToWhatItAlreadyHolds_WritesNothing()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SetAsync(issue.Key, "opus", null);
        var before = (await h.ReadAsync(issue.Key)).UpdatedAt;

        h.Time.Advance(TimeSpan.FromHours(1));
        await h.SetAsync(issue.Key, "opus", null);

        Assert.Single(await h.EventsAsync(issue.Key));
        Assert.Equal(before, (await h.ReadAsync(issue.Key)).UpdatedAt);
    }

    [Fact]
    public async Task ClearingAFieldThatIsAlreadyClear_WritesNothing()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();

        await h.SetAsync(issue.Key, "", "");

        Assert.Empty(await h.EventsAsync(issue.Key));
    }

    // ---- The one edge that is cut ----

    [Fact]
    public void SettingAnOverride_IsClosedToAnApiKey()
    {
        var guard = typeof(IssuePlaybookController)
            .GetMethod(nameof(IssuePlaybookController.PatchIssuePlaybook))!
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .SingleOrDefault();

        // No scope named, and no class-level attribute to inherit one from:
        // an override is a playbook's power routed through another table, and
        // an agent that could set one could raise its own budget.
        Assert.NotNull(guard);
        Assert.Null(guard.AcceptScope);

        Assert.Empty(typeof(IssuePlaybookController)
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false));
    }

    [Fact]
    public async Task ReadingAnOverride_IsOpenToAKey()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.SetAsync(issue.Key, "opus", "max");

        // It rides IssueDto, which IssuesController hands out to the hatch
        // scope: an agent is entitled to know what it is being spent on.
        var read = Value(await h.Issues.GetIssue(issue.Key, default));

        Assert.Equal("opus", read.ModelOverride);
        Assert.Equal("max", read.EffortOverride);
    }

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness
    {
        public required IssuePlaybookController Playbook { get; init; }
        public required IssuesController Issues { get; init; }
        public required IssueThreadController Thread { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required int ProjectId { get; init; }

        public async Task<IssueDto> FileAsync(string type = "task", string title = "a thing") =>
            Created(await Issues.CreateIssue(new IssueCreateRequest(ProjectId, type, title, null, null, null, null), default));

        public async Task<IssueDto> SetAsync(string key, string? model, string? effort) =>
            Value(await Playbook.PatchIssuePlaybook(key, new IssuePlaybookRequest(model, effort), default));

        public async Task<IssueDto> ReadAsync(string key) => Value(await Issues.GetIssue(key, default));

        /// <summary>The overrides' own events, in the order they were written.</summary>
        public async Task<IReadOnlyList<IssueEventDto>> EventsAsync(string key) =>
            Value(await Thread.GetEvents(key, default))
                .Where(e => e.Kind is EfHatchIssueEvent.ModelOverrideChanged or EfHatchIssueEvent.EffortOverrideChanged)
                .Reverse()
                .ToList();
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
        var caller = new StubCallerIdentity { Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now } };

        return new Harness
        {
            Playbook = new IssuePlaybookController(db, TestClaims.With(), caller, time),
            Issues = new IssuesController(db, new RankService(db), TestClaims.With(), caller, time),
            Thread = new IssueThreadController(db, caller, time),
            Time = time,
            ProjectId = aerie.Id,
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

    /// <summary>The plain-text reason on a refusal - what the UI puts on screen.</summary>
    private static string Reason(IActionResult? result) => result switch
    {
        ObjectResult o => o.Value?.ToString() ?? $"{o.StatusCode}",
        StatusCodeResult s => s.StatusCode.ToString(),
        null => "no result",
        _ => result.GetType().Name,
    };
}
