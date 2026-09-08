using System.Net;

namespace Aerie.Hatch.Tests;

/// <summary>
/// The runner on the board: what it says about itself between increments, and
/// what it does about what comes back.
/// </summary>
/// <remarks>
/// The heartbeat is at the top of a pass, which is the one moment no claim is
/// held - so every one of these is really a test that an instruction lands
/// <em>between</em> increments and never inside one.
/// </remarks>
public sealed class RunnersTests
{
    private const string Queue = "/api/hatch/work/queue";

    /// <summary>Where the harness's own runner name lands, escaped as a runner name has to be.</summary>
    private const string Beat = "/api/hatch/runners/test%3A%2Fcheckout";

    private static Guid OneTicket(Harness h, string key = "AER-1")
    {
        var token = Guid.NewGuid();

        h.Wire.Json("GET", Queue, new[] { Fixtures.Row(key) });
        h.Wire.Reply("POST", $"/api/hatch/issues/{key}/claim", HttpStatusCode.OK, Fixtures.Taken(token));
        h.Wire.Reply("POST", $"/api/hatch/issues/{key}/claim/heartbeat", HttpStatusCode.NoContent);
        h.Wire.Reply("DELETE", $"/api/hatch/issues/{key}/claim", HttpStatusCode.NoContent);
        h.Wire.Json("GET", $"/api/hatch/work/{key}", Fixtures.Work(key, from: "In Review"));
        h.Wire.Json("POST", $"/api/hatch/issues/{key}/work-log", Fixtures.WorkLogRow());
        h.Wire.Json("GET", $"/api/hatch/issues/{key}/questions", Array.Empty<QuestionDto>());

        return token;
    }

    /// <summary>What the board answers a heartbeat with, when it has anything to say.</summary>
    private static void Instructs(
        Harness h, string state, string? under = null, int? maxRuns = null,
        decimal? maxSpend = null, DateTimeOffset? untilAt = null) =>
        h.Wire.Json("POST", Beat, new RunnerInstructionDto(state, under, maxRuns, maxSpend, untilAt));

    private static IReadOnlyList<Call> Beats(Harness h) => h.Wire.To("POST", Beat);

    // ---- Saying hello ----

    [Fact]
    public async Task A_pass_says_it_is_here_before_it_takes_anything()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "running");

        await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default);

        // The order is the point: the board knows this runner exists before it
        // holds a ticket, so a row that is working something can never be a row
        // nobody has seen.
        var calls = h.Wire.Calls;
        Assert.Equal(Beat, calls[0].Path);
        Assert.True(
            calls.TakeWhile(c => c.Path != "/api/hatch/issues/AER-1/claim").Any(c => c.Path == Beat),
            "the runner said it was here before it claimed anything");
    }

    [Fact]
    public async Task A_loop_says_it_is_a_loop_and_carries_the_bounds_it_started_with()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "running");

        await new GoToWorkCommand(h.Runtime).RunAsync(
            ["--once", "--under", "AER-930", "--max-runs", "4", "--max-spend", "12.50"], default);

        var beat = Assert.Single(Beats(h)).Read<RunnerHeartbeatRequest>();

        // Sent on every beat and written by the server only on the first, which
        // is what makes the row show the flags this process actually started
        // with without a restart undoing somebody's edit.
        Assert.Equal("once", beat.Kind);
        Assert.Equal("AER-930", beat.Under);
        Assert.Equal(4, beat.MaxRuns);
        Assert.Equal(12.50m, beat.MaxSpend);
    }

    [Fact]
    public async Task Every_pass_says_it_is_here_and_says_what_it_last_did()
    {
        using var h = new Harness();
        OneTicket(h);

        // The cap comes back on the answer, because the board is what a loop's
        // bounds live on once it has a row - the flags only ever seeded it.
        Instructs(h, "running", maxRuns: 2);

        await new GoToWorkCommand(h.Runtime).RunAsync(["--max-runs", "2"], default);

        // Three for two increments: one at the top of each pass, and one at the
        // top of the pass that read the cap and stopped - which is what leaves
        // the row saying how the night ended rather than what it was doing an
        // increment ago.
        var beats = Beats(h);
        Assert.Equal(3, beats.Count);
        Assert.Equal("loop", beats[0].Read<RunnerHeartbeatRequest>().Kind);

        // Each beat after the first carries what the pass before it came to, so
        // a page that cannot see a terminal still reads like one.
        Assert.Contains("AER-1", beats[1].Read<RunnerHeartbeatRequest>().Line);
        Assert.Contains("AER-1", beats[2].Read<RunnerHeartbeatRequest>().Line);
    }

    [Fact]
    public async Task An_idle_pass_says_what_it_is_waiting_for()
    {
        using var h = new Harness();
        h.Wire.Json("GET", Queue, Array.Empty<QueueEntryDto>());
        h.Wire.Json("GET", "/api/hatch/questions", Array.Empty<QuestionDto>());
        Instructs(h, "running");

        await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default);

        Assert.Equal("reading the board", Assert.Single(Beats(h)).Read<RunnerHeartbeatRequest>().Line);
    }

    [Fact]
    public async Task A_Hatch_too_old_to_have_the_route_changes_nothing()
    {
        using var h = new Harness();
        OneTicket(h);

        // Nothing stubs the heartbeat, so it 404s the way an older Hatch would.
        // A runner with no instruction is a runner on the flags it started with,
        // which is exactly what it was before any of this existed.
        Assert.Equal(0, await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default));

        Assert.Single(h.Sessions.Spawned);
        Assert.Single(Beats(h));
    }

    // ---- Being told to stop ----

    [Fact]
    public async Task Stopping_ends_the_night_without_taking_another_ticket()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "stopping");

        Assert.Equal(0, await new GoToWorkCommand(h.Runtime).RunAsync([], default));

        // Nothing spawned, nothing claimed, and the reason said out loud - a
        // loop that went quiet would read like a crash.
        Assert.Empty(h.Sessions.Spawned);
        Assert.Empty(h.Wire.To("POST", "/api/hatch/issues/AER-1/claim"));
        Assert.Contains(h.Say.Said, l => l.Contains("the board asked this runner to stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopping_waits_for_the_increment_already_in_flight()
    {
        using var h = new Harness();
        OneTicket(h);

        // Running on the first beat, stopping on the second: the increment the
        // first pass started finishes, is billed, and only then does the loop
        // read the second answer and leave.
        h.Wire.Once("POST", Beat, HttpStatusCode.OK, Answer("running"));
        Instructs(h, "stopping");

        await new GoToWorkCommand(h.Runtime).RunAsync([], default);

        Assert.Single(h.Sessions.Spawned);
        Assert.Single(h.Wire.To("DELETE", "/api/hatch/issues/AER-1/claim"));
        Assert.Contains(h.Say.Said, l => l.Contains("the board asked this runner to stop", StringComparison.Ordinal));
    }

    // ---- Being told to hold ----

    [Fact]
    public async Task Paused_keeps_saying_it_is_here_and_takes_nothing()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "paused");

        using var interrupting = new CancellationTokenSource();
        var running = new GoToWorkCommand(h.Runtime).RunAsync(["--interval", "1"], interrupting.Token);

        // Two beats is the property under test: a paused loop that stopped
        // heartbeating would drift from paused to gone while it was doing
        // exactly what it was asked to.
        await Harness.Eventually(() => Beats(h).Count >= 2, "a paused runner to heartbeat twice");
        await interrupting.CancelAsync();
        await running;

        Assert.Empty(h.Sessions.Spawned);
        Assert.Empty(h.Wire.To("POST", "/api/hatch/issues/AER-1/claim"));
        Assert.Contains(h.Say.Said, l => l.Contains("has this runner paused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Set_running_again_it_picks_up_where_it_left_off()
    {
        using var h = new Harness();
        OneTicket(h);

        h.Wire.Once("POST", Beat, HttpStatusCode.OK, Answer("paused"));
        Instructs(h, "running", maxRuns: 1);

        await new GoToWorkCommand(h.Runtime).RunAsync(["--interval", "1", "--max-runs", "1"], default);

        // The pause cost one interval and nothing else: no ticket was held
        // through it, and the increment ran on the pass after it.
        Assert.Single(h.Sessions.Spawned);
    }

    // ---- Being told what it may spend ----

    [Fact]
    public async Task A_cap_set_on_the_board_bounds_a_night_that_was_started_without_one()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "running", maxRuns: 1);

        await new GoToWorkCommand(h.Runtime).RunAsync([], default);

        Assert.Single(h.Sessions.Spawned);
        Assert.Contains(h.Say.Said, l => l.Contains("--max-runs 1 reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cap_taken_off_on_the_board_takes_it_off_the_loop()
    {
        using var h = new Harness();
        OneTicket(h);

        // The row was seeded with --max-runs 1 and somebody cleared it, so the
        // absence in the answer is an instruction and not a silence. Read the
        // other way - "leave the flag alone" - a cap could never be lifted from
        // the page that set it.
        Instructs(h, "running");

        using var interrupting = new CancellationTokenSource();
        var running = new GoToWorkCommand(h.Runtime).RunAsync(["--max-runs", "1"], interrupting.Token);

        await Harness.Eventually(() => h.Sessions.Spawned.Count >= 2, "a second increment past the lifted cap");
        await interrupting.CancelAsync();
        await running;
    }

    [Fact]
    public async Task A_scope_set_on_the_board_is_where_the_next_pass_looks()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "running", under: "AER-930", maxRuns: 1);

        await new GoToWorkCommand(h.Runtime).RunAsync([], default);

        // The queue is read under the epic the board named, though no --under
        // was ever typed at this process.
        Assert.Contains(h.Wire.To("GET", Queue), c => c.Query.Contains("ancestorKey=AER-930", StringComparison.Ordinal));
    }

    // ---- One increment, and out ----

    [Fact]
    public async Task Once_sends_one_beat_and_obeys_nothing()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "stopping");

        await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default);

        // There is no second pass for an instruction to apply to, so reading
        // one would only mean skipping the single increment that was asked for.
        Assert.Single(h.Sessions.Spawned);
        Assert.Single(Beats(h));
        Assert.Equal("once", Assert.Single(Beats(h)).Read<RunnerHeartbeatRequest>().Kind);
        Assert.DoesNotContain(h.Say.Said, l => l.Contains("asked this runner to stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Work_says_it_is_here_once_and_reads_nothing_back()
    {
        using var h = new Harness();
        OneTicket(h);
        Instructs(h, "stopping");

        await new WorkCommand(h.Runtime).RunAsync(["AER-1"], default);

        var beat = Assert.Single(Beats(h)).Read<RunnerHeartbeatRequest>();
        Assert.Equal("once", beat.Kind);
        Assert.Equal("one increment on AER-1", beat.Line);
        Assert.Single(h.Sessions.Spawned);
    }

    [Fact]
    public async Task A_dry_run_says_nothing_because_it_spends_nothing()
    {
        using var h = new Harness();
        OneTicket(h);

        await new WorkCommand(h.Runtime).RunAsync(["AER-1", "--dry-run"], default);

        // The same argument the dry run makes about the claim: a run that takes
        // nothing and spends nothing has nothing to say it is doing.
        Assert.Empty(Beats(h));
    }

    private static string Answer(string state) =>
        System.Text.Json.JsonSerializer.Serialize(
            new RunnerInstructionDto(state, null, null, null, null), Fixtures.Json);
}
