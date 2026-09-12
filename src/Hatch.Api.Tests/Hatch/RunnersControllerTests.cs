using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Modules.Hatch;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// The runners: what a heartbeat writes and what it must never overwrite, what
/// the read says about a runner that has stopped answering, and the one
/// property the control surface rests on - that an agent cannot raise its own
/// bounds.
/// </summary>
public class RunnersControllerTests
{
    private const string Runner = "here:/checkouts/one";

    // ---- Saying hello ----

    [Fact]
    public async Task AHeartbeat_PutsTheRunnerOnTheBoard()
    {
        var h = await NewAsync();

        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Kind: "loop", Line: "reading the board"));

        var runner = Assert.Single(await h.ListAsync());
        Assert.Equal(Runner, runner.Name);
        Assert.Equal("loop", runner.Kind);
        Assert.Equal("running", runner.State);
        Assert.Equal("reading the board", runner.Line);
        Assert.Equal(Now, runner.FirstSeenAt);
        Assert.Equal(Now, runner.LastSeenAt);
        Assert.Null(runner.ClaimKey);
    }

    [Fact]
    public async Task ASecondHeartbeat_MovesTheClockAndNotTheFirstSighting()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Kind: "loop"));

        h.Time.Advance(TimeSpan.FromMinutes(4));
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Kind: "loop"));

        var runner = Assert.Single(await h.ListAsync());
        Assert.Equal(Now, runner.FirstSeenAt);
        Assert.Equal(Now.AddMinutes(4), runner.LastSeenAt);
    }

    [Fact]
    public async Task AnEscapedName_IsTheNameTheRunnerCallsItself()
    {
        var h = await NewAsync();

        // Kestrel leaves %2F encoded in the path on purpose, so a runner whose
        // name is a path arrives here still carrying them.
        await h.BeatAsync("here:%2Fcheckouts%2Fone", new RunnerHeartbeatRequest(Kind: "loop"));

        var runner = Assert.Single(await h.ListAsync());
        Assert.Equal(Runner, runner.Name);
    }

    [Fact]
    public async Task ANamelessRunner_IsRefused()
    {
        var h = await NewAsync();

        var refusal = await h.Runners.Heartbeat("   ", new RunnerHeartbeatRequest(), default);

        Assert.Contains("a runner names itself", Reason(refusal.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWideLine_IsTruncatedRatherThanEndingTheNight()
    {
        var h = await NewAsync();

        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Line: new string('x', 900)));

        var runner = Assert.Single(await h.ListAsync());
        Assert.Equal(EfHatchRunner.MaxLineLength, runner.Line!.Length);
    }

    [Fact]
    public async Task AnEmptyLine_ClearsItAndAnAbsentOneLeavesItAlone()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Line: "waiting"));

        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());
        Assert.Equal("waiting", (await h.OneAsync()).Line);

        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Line: ""));
        Assert.Null((await h.OneAsync()).Line);
    }

    // ---- What it is working ----

    [Fact]
    public async Task ARunnerHoldingATicket_SaysWhichOneAndWhatItIsDoing()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ClaimAsync(issue.Key, Runner, chatter: "make test-api");
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Line: "claimed a ticket"));

        var runner = await h.OneAsync();

        // Read off the issue at request time rather than copied onto the row:
        // an operator clearing a claim clears this too, on the next poll.
        Assert.Equal(issue.Key, runner.ClaimKey);
        Assert.Equal("make test-api", runner.Line);
    }

    [Fact]
    public async Task AClaimThatWasCleared_LeavesTheRunnerIdleWithNoSecondCopyToTidy()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ClaimAsync(issue.Key, Runner, chatter: "make test-api");
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Line: "claimed a ticket"));

        await h.ReleaseAsync(issue.Key);

        var runner = await h.OneAsync();
        Assert.Null(runner.ClaimKey);
        Assert.Equal("claimed a ticket", runner.Line);
    }

    [Fact]
    public async Task AnExpiredClaim_IsNotSomethingARunnerIsStillWorking()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ClaimAsync(issue.Key, Runner, chatter: "make test-api");
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        // Past the lease, and the same arithmetic the board does: a claim
        // nothing has refreshed is not a claim.
        h.Time.Advance(TimeSpan.FromSeconds(TestClaims.Ttl + 1));
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        Assert.Null((await h.OneAsync()).ClaimKey);
    }

    [Fact]
    public async Task AnotherRunnersClaim_IsNotDrawnOnThisRow()
    {
        var h = await NewAsync();
        var issue = await h.FileAsync();
        await h.ClaimAsync(issue.Key, "elsewhere:/tree", chatter: "make test-api");
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        Assert.Null((await h.OneAsync()).ClaimKey);
    }

    // ---- Aging ----

    [Fact]
    public async Task ARunnerThatStoppedAnswering_IsStillDrawnUntilTenHorizonsHaveGone()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        // Gone, but on the page: a loop that died at midnight is a fact
        // somebody wants to see in the morning, not a row that quietly vanished.
        h.Time.Advance(TimeSpan.FromSeconds(Horizon * 3));
        var gone = await h.OneAsync();
        Assert.Equal(Now, gone.LastSeenAt);
        Assert.Equal(Horizon, gone.GoneAfterSeconds);

        // And then not, dropped by the WHERE on the read with nothing swept.
        h.Time.Advance(TimeSpan.FromSeconds(Horizon * 7 + 1));
        Assert.Empty(await h.ListAsync());
    }

    [Fact]
    public async Task ADroppedRunnerThatComesBack_KeepsTheBoundsItHadRatherThanBeingSeededAfresh()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(MaxRuns: 5));
        await h.PatchAsync(Runner, new RunnerPatchRequest(MaxRuns: "40"));

        h.Time.Advance(TimeSpan.FromSeconds(Horizon * 20));
        Assert.Empty(await h.ListAsync());

        // The row was never deleted, only aged out of the read - so a checkout
        // that starts a loop again the next evening is still bounded the way
        // somebody set it.
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(MaxRuns: 5));
        Assert.Equal(40, (await h.OneAsync()).MaxRuns);
    }

    [Fact]
    public async Task TheMostRecentlyHeardFrom_IsFirst()
    {
        var h = await NewAsync();
        await h.BeatAsync("first:/tree", new RunnerHeartbeatRequest());

        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.BeatAsync("second:/tree", new RunnerHeartbeatRequest());

        Assert.Equal(["second:/tree", "first:/tree"], (await h.ListAsync()).Select(r => r.Name));
    }

    // ---- The seed ----

    [Fact]
    public async Task TheFirstHeartbeat_SeedsTheBoundsTheProcessStartedWith()
    {
        var h = await NewAsync();

        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(
            Kind: "loop", Under: "AER-930", MaxRuns: 12, MaxSpend: 40m, UntilAt: Now.AddHours(6)));

        var runner = await h.OneAsync();
        Assert.Equal("AER-930", runner.Under);
        Assert.Equal(12, runner.MaxRuns);
        Assert.Equal(40m, runner.MaxSpend);
        Assert.Equal(Now.AddHours(6), runner.UntilAt);
    }

    [Fact]
    public async Task ALaterHeartbeat_NeverSeedsOverWhatAPersonSet()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(MaxRuns: 12, MaxSpend: 40m));
        await h.PatchAsync(Runner, new RunnerPatchRequest(MaxRuns: "3", MaxSpend: "5"));

        // A restart sends the flags it was started with all over again. If that
        // re-seeded, an edit made at midnight would be undone by the loop's own
        // half-hourly restart - which is the failure this rule exists for.
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(MaxRuns: 12, MaxSpend: 40m));

        var runner = await h.OneAsync();
        Assert.Equal(3, runner.MaxRuns);
        Assert.Equal(5m, runner.MaxSpend);
    }

    [Fact]
    public async Task AHeartbeat_NeverWritesTheState()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());
        await h.PatchAsync(Runner, new RunnerPatchRequest(State: "stopping"));

        var instruction = await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        // It reads the instruction back rather than resetting it: a runner that
        // could un-stop itself by saying hello is a runner nobody can stop.
        Assert.Equal("stopping", instruction.State);
        Assert.Equal("stopping", (await h.OneAsync()).State);
    }

    [Fact]
    public async Task TheKind_FollowsWhicheverCommandIsRunningNow()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Kind: "loop"));

        // One checkout runs both commands. A row that remembered the loop would
        // offer buttons for a process that ended an hour ago.
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest(Kind: "once"));

        Assert.Equal("once", (await h.OneAsync()).Kind);
    }

    // ---- What the board asks for ----

    [Fact]
    public async Task TheThreeStates_AreTheOnesTheLoopKnows()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        foreach (var state in EfHatchRunner.States)
        {
            Assert.Equal(state, (await h.PatchAsync(Runner, new RunnerPatchRequest(State: state))).State);
            Assert.Equal(state, (await h.BeatAsync(Runner, new RunnerHeartbeatRequest())).State);
        }
    }

    [Fact]
    public async Task AStateNobodyImplements_IsRefusedInWords()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        var refusal = await h.Runners.PatchRunner(Runner, new RunnerPatchRequest(State: "halt"), default);

        Assert.Equal(
            "a runner is running, paused, stopping - not \"halt\"",
            Reason(refusal.Result));
    }

    [Fact]
    public async Task TheBounds_AreFoldedIntoWhatTheLoopReadsBack()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        await h.PatchAsync(Runner, new RunnerPatchRequest(
            Under: "aer-930", MaxRuns: "12", MaxSpend: "40.00", UntilAt: "2026-09-09T06:00:00Z"));

        var instruction = await h.BeatAsync(Runner, new RunnerHeartbeatRequest());
        Assert.Equal("AER-930", instruction.Under);
        Assert.Equal(12, instruction.MaxRuns);
        Assert.Equal(40m, instruction.MaxSpend);
        Assert.Equal(new DateTimeOffset(2026, 9, 9, 6, 0, 0, TimeSpan.Zero), instruction.UntilAt);
    }

    [Fact]
    public async Task AnAbsentFieldLeavesOneAloneAndAnEmptyOneTakesItOff()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());
        await h.PatchAsync(Runner, new RunnerPatchRequest(
            Under: "AER-930", MaxRuns: "12", MaxSpend: "40", UntilAt: "2026-09-09T06:00:00Z"));

        var still = await h.PatchAsync(Runner, new RunnerPatchRequest(State: "paused"));
        Assert.Equal("AER-930", still.Under);
        Assert.Equal(12, still.MaxRuns);

        var cleared = await h.PatchAsync(Runner, new RunnerPatchRequest(
            Under: "", MaxRuns: "", MaxSpend: "", UntilAt: ""));

        // The tri-state, all the way through: a cap taken off is a loop with no
        // cap, which a nullable number could not have said.
        Assert.Null(cleared.Under);
        Assert.Null(cleared.MaxRuns);
        Assert.Null(cleared.MaxSpend);
        Assert.Null(cleared.UntilAt);
    }

    [Fact]
    public async Task ARefusedFieldWritesNothingAtAll()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        var refusal = await h.Runners.PatchRunner(
            Runner, new RunnerPatchRequest(State: "paused", MaxSpend: "lots"), default);

        Assert.Contains("--max-spend takes an amount", Reason(refusal.Result), StringComparison.Ordinal);

        // The good half of a bad request is not half-applied.
        Assert.Equal("running", (await h.OneAsync()).State);
    }

    [Fact]
    public async Task ABareDate_IsRefusedBecauseAStopHourIsAnInstant()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        var refusal = await h.Runners.PatchRunner(Runner, new RunnerPatchRequest(UntilAt: "2026-09-09"), default);

        Assert.Contains("--until takes an instant", Reason(refusal.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomethingThatIsNotAnIssueKey_IsNotAScope()
    {
        var h = await NewAsync();
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        var refusal = await h.Runners.PatchRunner(Runner, new RunnerPatchRequest(Under: "the epic"), default);

        Assert.Contains("--under names an issue", Reason(refusal.Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunnerNobodyHasEverSeen_IsNotSomethingToPatch()
    {
        var h = await NewAsync();

        var refusal = await h.Runners.PatchRunner("nowhere:/tree", new RunnerPatchRequest(State: "paused"), default);

        // The table records what has spoken to this Hatch. Typing a hostname
        // into a URL does not make a runner.
        Assert.IsType<NotFoundResult>(refusal.Result);
    }

    // ---- The edge that is cut ----

    [Fact]
    public async Task SettingWhatARunnerMaySpend_IsClosedToAKey()
    {
        var h = await NewAsync(program: true);
        await h.BeatAsync(Runner, new RunnerHeartbeatRequest());

        var refusal = await h.Runners.PatchRunner(Runner, new RunnerPatchRequest(MaxSpend: "1000"), default);

        Assert.Equal(403, ((ObjectResult)refusal.Result!).StatusCode);
        Assert.Null((await h.OneAsync()).MaxSpend);
    }

    [Fact]
    public void TheThreeRoutes_AreScopedTheWayTheClaimAndThePlaybookAre()
    {
        // The heartbeat and the read are a dispatcher's - a runner that could
        // not say it was alive would leave a page that could only be empty. The
        // write is a person's, because it is the budget.
        Assert.Equal(ApiKeyScopes.Hatch, Scope(nameof(RunnersController.Heartbeat))!.AcceptScope);
        Assert.Equal(ApiKeyScopes.Hatch, Scope(nameof(RunnersController.GetRunners))!.AcceptScope);

        var patch = Scope(nameof(RunnersController.PatchRunner));
        Assert.NotNull(patch);
        Assert.Null(patch.AcceptScope);

        // And no class-level attribute for the PATCH to inherit a scope from.
        Assert.Empty(typeof(RunnersController).GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false));
    }

    private static RequireAdminAttribute? Scope(string method) =>
        typeof(RunnersController).GetMethod(method)!
            .GetCustomAttributes(typeof(RequireAdminAttribute), inherit: false)
            .Cast<RequireAdminAttribute>()
            .SingleOrDefault();

    // ---- Harness ----

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The horizon these tests judge against, short enough to step over inside one.</summary>
    private const int Horizon = 60;

    private sealed class Harness
    {
        public required RunnersController Runners { get; init; }
        public required IssuesController Issues { get; init; }
        public required HatchContext Db { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required int ProjectId { get; init; }

        public async Task<IssueDto> FileAsync(string title = "a thing") =>
            Created(await Issues.CreateIssue(new IssueCreateRequest(ProjectId, "task", title, null, null, null, null), default));

        public async Task<RunnerInstructionDto> BeatAsync(string name, RunnerHeartbeatRequest request) =>
            Value(await Runners.Heartbeat(name, request, default));

        public async Task<RunnerDto> PatchAsync(string name, RunnerPatchRequest request) =>
            Value(await Runners.PatchRunner(name, request, default));

        public async Task<IReadOnlyList<RunnerDto>> ListAsync() => Value(await Runners.GetRunners(default));

        public async Task<RunnerDto> OneAsync() => Assert.Single(await ListAsync());

        /// <summary>
        /// A lease, written straight onto the row. The endpoint that normally
        /// writes one is built out of conditional UPDATEs the in-memory
        /// provider refuses - see HatchDatabase - and nothing here is testing
        /// the claim.
        /// </summary>
        public async Task ClaimAsync(string key, string runner, string? chatter = null)
        {
            var issue = await Find(key);
            issue.ClaimToken = Guid.NewGuid();
            issue.ClaimedBy = "hatch";
            issue.ClaimRunner = runner;
            issue.ClaimedAt = Time.GetUtcNow();
            issue.ClaimHeartbeatAt = Time.GetUtcNow();
            issue.ClaimChatter = chatter;
            issue.ClaimChatterAt = chatter is null ? null : Time.GetUtcNow();
            await Db.SaveChangesAsync();
        }

        public async Task ReleaseAsync(string key)
        {
            var issue = await Find(key);
            issue.ClaimToken = null;
            issue.ClaimRunner = null;
            issue.ClaimHeartbeatAt = null;
            issue.ClaimChatter = null;
            await Db.SaveChangesAsync();
        }

        private async Task<EfHatchIssue> Find(string key)
        {
            IssueKey.TryParse(key, out var projectKey, out var number);
            return await Db.Issues.Include(i => i.Project).WithKey(projectKey, number).FirstAsync();
        }
    }

    private static async Task<Harness> NewAsync(bool program = false)
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var hatch = new EfHatchProject { Key = "AER", Name = "Hatch", CreatedAt = Now };
        db.Add(hatch);
        db.Add(new EfHatchStatus { Name = "inbox", SortOrder = 10 });
        await db.SaveChangesAsync();

        var time = new FakeTimeProvider(Now);
        var claims = TestClaims.With();
        var runners = new Runners(Options.Create(new HatchOptions { RunnerGoneAfterSeconds = Horizon }));

        var caller = program
            ? new StubCaller
            {
                Key = new EfApiKey
                {
                    Name = "hatch",
                    Hash = [1],
                    Prefix = "hatch_ak_x",
                    Scopes = [ApiKeyScopes.Hatch],
                    CreatedAt = Now,
                },
            }
            : new StubCaller { Person = new EfPerson { Name = "Nathan", CreatedAt = Now, UpdatedAt = Now } };

        return new Harness
        {
            Runners = new RunnersController(db, runners, claims, caller, time),
            Issues = new IssuesController(db, new RankService(db), new StubActorDirectory(), claims, caller, time),
            Db = db,
            Time = time,
            ProjectId = hatch.Id,
        };
    }

    /// <summary>Whoever is holding the phone: a person, or the key an agent carries.</summary>
    private sealed class StubCaller : ICallerIdentity
    {
        public EfPerson? Person { get; init; }

        public EfApiKey? Key { get; init; }

        public Task<EfAuthGrant?> GrantAsync(CancellationToken ct) => Task.FromResult<EfAuthGrant?>(null);

        public Task<Guid?> PersonIdAsync(CancellationToken ct) => Task.FromResult(Person?.Id);

        public Task<EfPerson?> PersonAsync(CancellationToken ct) => Task.FromResult(Person);

        public Task<EfApiKey?> ApiKeyAsync(CancellationToken ct) => Task.FromResult(Key);

        public Task<Actor?> LocalAsync(CancellationToken ct) => Task.FromResult<Actor?>(null);

        public Task<bool> IsProgramAsync(CancellationToken ct) => Task.FromResult(Key is not null);

        public Task<string> ActorNameAsync(CancellationToken ct) =>
            Task.FromResult(Person?.Name ?? Key?.Name ?? CallerIdentity.Unattributed);
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
