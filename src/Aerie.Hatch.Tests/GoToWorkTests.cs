using System.Net;

namespace Aerie.Hatch.Tests;

/// <summary>
/// The loop: what a pass does in what order, what it waits for, and what it
/// stops for.
/// </summary>
public sealed class GoToWorkTests
{
    private const string Queue = "/api/hatch/work/queue";

    private static string Held(string runner) =>
        $"\"aerie-hatch is working this from {runner}, last heard from 8 seconds ago\"";

    /// <summary>A board with one clear ticket on it, and everything an increment on it reads.</summary>
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

    [Fact]
    public async Task A_pass_claims_before_it_resets_the_workspace_and_before_it_spawns()
    {
        using var h = new Harness();
        OneTicket(h);

        // What had already happened by the time the tree was asked to make
        // itself current. A ticket held is a ticket nothing else will start, and
        // a reset before the claim would be a fetch spent on an increment that
        // never happens.
        var claimedByThen = false;
        var spawnedByThen = false;
        h.Workspace.Watching = () =>
        {
            claimedByThen = h.Wire.Count("POST", "/api/hatch/issues/AER-1/claim") == 1;
            spawnedByThen = h.Sessions.Spawned.Count > 0;
        };

        Assert.Equal(0, await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default));

        Assert.True(claimedByThen, "the ticket was claimed before the workspace was reset");
        Assert.False(spawnedByThen, "nothing was spawned before the workspace was reset");
        Assert.Equal(1, h.Workspace.Prepared);
        Assert.Single(h.Sessions.Spawned);

        // ...and let go of when the increment ended.
        Assert.Single(h.Wire.To("DELETE", "/api/hatch/issues/AER-1/claim"));
    }

    [Fact]
    public async Task A_tree_that_will_not_reset_ends_the_night_and_gives_the_ticket_back()
    {
        using var h = new Harness();
        OneTicket(h);
        h.Workspace.Answer = Reset.Never;

        await new GoToWorkCommand(h.Runtime).RunAsync([], default);

        // Nothing was spawned onto a tree the loop could not make current, and
        // the lease did not sit there until its TTL.
        Assert.Empty(h.Sessions.Spawned);
        Assert.Single(h.Wire.To("DELETE", "/api/hatch/issues/AER-1/claim"));
        Assert.Contains(h.Say.Said, l => l.Contains("the workspace could not be reset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fetch_that_did_not_answer_is_a_wait_and_the_ticket_still_goes_back()
    {
        using var h = new Harness();
        OneTicket(h);
        h.Workspace.Answer = Reset.Later;

        await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default);

        Assert.Empty(h.Sessions.Spawned);
        Assert.Single(h.Wire.To("DELETE", "/api/hatch/issues/AER-1/claim"));
        Assert.Contains(h.Say.Complained, l => l.Contains("the workspace is not ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_busy_board_waits_rather_than_ending_and_does_not_read_like_an_empty_one()
    {
        using var h = new Harness();
        var rows = Enumerable.Range(1, 5).Select(n => Fixtures.Row($"AER-{n}")).ToArray();

        h.Wire.Json("GET", Queue, rows);
        foreach (var row in rows)
            h.Wire.Reply("POST", $"/api/hatch/issues/{row.Issue.Key}/claim", HttpStatusCode.Conflict,
                Held("other:/tree"));

        await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default);

        Assert.Contains(h.Say.Said, l => l.Contains("being worked by another runner", StringComparison.Ordinal));
        Assert.Contains(h.Say.Said, l => l.Contains("other:/tree", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Say.Said, l => l.Contains("nothing on the board", StringComparison.Ordinal));

        // Nothing was spawned, and the tree was never touched for a pass that
        // had nothing to do.
        Assert.Empty(h.Sessions.Spawned);
        Assert.Equal(0, h.Workspace.Prepared);
        Assert.Contains(h.Say.Said, l => l.Contains("--once, and the pass is done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_board_reads_like_an_empty_board()
    {
        using var h = new Harness();
        h.Wire.Json("GET", Queue, Array.Empty<QueueEntryDto>());
        h.Wire.Json("GET", "/api/hatch/questions", Array.Empty<QuestionDto>());

        await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default);

        Assert.Contains(h.Say.Said, l => l.Contains("nothing on the board is an agent's to move", StringComparison.Ordinal));

        // `--once` is one pass and out, so it does not promise to ask again.
        Assert.DoesNotContain(h.Say.Said, l => l.Contains("waiting, and asking again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_interrupt_gives_the_ticket_back_and_still_prints_the_tally()
    {
        using var h = new Harness();
        OneTicket(h);
        using var interrupting = new CancellationTokenSource();

        h.Sessions.Behaviour = FakeSessions.UntilStopped();

        var running = new GoToWorkCommand(h.Runtime).RunAsync([], interrupting.Token);
        await h.Sessions.Started.Task;
        await interrupting.CancelAsync();
        await running;

        Assert.Single(h.Wire.To("DELETE", "/api/hatch/issues/AER-1/claim"));
        Assert.Contains(h.Say.Said, l => l.Contains("increment(s) in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_loop_in_this_checkout_is_refused_and_the_first_keeps_its_lock()
    {
        using var h = new Harness();
        h.Wire.Json("GET", Queue, Array.Empty<QueueEntryDto>());
        h.Wire.Json("GET", "/api/hatch/questions", Array.Empty<QuestionDto>());

        using var held = LoopLock.Take(h.Root, h.Temp, out _);
        Assert.NotNull(held);

        Assert.Equal(1, await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default));
        Assert.Contains(h.Say.Complained, l => l.Contains("already running in", StringComparison.Ordinal));

        // Nothing was read and nothing was spawned: the refusal is before the
        // board, because a loop that cannot run should not be asking.
        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task Two_loops_in_two_checkouts_both_get_going()
    {
        using var first = new Harness();
        using var second = new Harness();

        foreach (var h in (Harness[])[first, second])
        {
            h.Wire.Json("GET", Queue, Array.Empty<QueueEntryDto>());
            h.Wire.Json("GET", "/api/hatch/questions", Array.Empty<QuestionDto>());
        }

        Assert.Equal(0, await new GoToWorkCommand(first.Runtime).RunAsync(["--once"], default));
        Assert.Equal(0, await new GoToWorkCommand(second.Runtime).RunAsync(["--once"], default));

        Assert.DoesNotContain(first.Say.Complained, l => l.Contains("already running", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Say.Complained, l => l.Contains("already running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_loop_carries_no_flag_of_its_own_across_a_night()
    {
        using var h = new Harness();
        OneTicket(h);

        await new GoToWorkCommand(h.Runtime).RunAsync(["--once"], default);

        // `work --model` is one operator's opinion about one increment; what the
        // loop carries is the playbook's, which already has the ticket's own
        // overrides folded into it.
        var spawned = Assert.Single(h.Sessions.Spawned);
        Assert.Equal("opus", spawned.Model);
        Assert.Equal("high", spawned.Effort);
        Assert.Equal(h.Root, spawned.Root);
    }

    [Fact]
    public async Task It_takes_where_to_look_or_a_ticket_and_a_ticket_is_never_the_answer()
    {
        using var h = new Harness();

        Assert.Equal(1, await new GoToWorkCommand(h.Runtime).RunAsync(["AER-1"], default));
        Assert.Contains(h.Say.Complained, l => l.Contains("does not take a ticket", StringComparison.Ordinal));

        Assert.Equal(1, await new GoToWorkCommand(h.Runtime).RunAsync(["AER-1", "--under", "AER-9"], default));
        Assert.Contains(h.Say.Complained, l => l.Contains("not both", StringComparison.Ordinal));

        // Zero is refused rather than clamped: it reads as "as fast as possible"
        // and means a board asked the same question thousands of times a minute.
        Assert.Equal(1, await new GoToWorkCommand(h.Runtime).RunAsync(["--interval", "0"], default));
        Assert.Equal(1, await new GoToWorkCommand(h.Runtime).RunAsync(["--until", "tea time"], default));

        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task A_stop_file_that_is_already_there_is_refused_rather_than_read_as_an_empty_board()
    {
        using var h = new Harness();
        var stop = Path.Combine(h.Temp, "stop");
        File.WriteAllText(stop, "");

        Assert.Equal(1, await new GoToWorkCommand(h.Runtime).RunAsync(["--stop-file", stop], default));
        Assert.Contains(h.Say.Complained, l => l.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Max_runs_bounds_the_night()
    {
        using var h = new Harness();
        OneTicket(h);

        await new GoToWorkCommand(h.Runtime).RunAsync(["--max-runs", "2"], default);

        Assert.Equal(2, h.Sessions.Spawned.Count);
        Assert.Equal(2, h.Wire.To("DELETE", "/api/hatch/issues/AER-1/claim").Count);
        Assert.Contains(h.Say.Said, l => l.Contains("--max-runs 2 reached", StringComparison.Ordinal));
    }
}
