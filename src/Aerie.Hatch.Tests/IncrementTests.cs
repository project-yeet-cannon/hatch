using System.Diagnostics;
using System.Net;

namespace Aerie.Hatch.Tests;

/// <summary>
/// One increment, and the two things that must still be true when its lease goes
/// underneath it: the meter reading goes up, and nothing else does.
/// </summary>
public sealed class IncrementTests
{
    private static async Task<(Claim Claim, Harness Harness)> HoldingAsync(Harness h, string key, Guid token)
    {
        h.Wire.Reply("POST", $"/api/hatch/issues/{key}/claim", HttpStatusCode.OK, Fixtures.Taken(token));
        h.Wire.Reply("DELETE", $"/api/hatch/issues/{key}/claim", HttpStatusCode.NoContent);

        var (claim, _) = await Claim.TakeAsync(h.Client, key, "test:/checkout", default, Harness.Beat);
        return (claim!, h);
    }

    [Fact]
    public async Task An_increment_posts_its_bill_and_asks_the_board_where_the_ticket_ended_up()
    {
        using var h = new Harness();
        var token = Guid.NewGuid();
        var (claim, _) = await HoldingAsync(h, "AER-1", token);

        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim/heartbeat", HttpStatusCode.NoContent);
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/work-log", Fixtures.WorkLogRow());
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1", from: "In Review"));
        h.Wire.Json("GET", "/api/hatch/issues/AER-1/questions", Array.Empty<QuestionDto>());

        var report = await h.Runtime.Increment().RunAsync(
            Fixtures.Work("AER-1"), h.Root, "opus", "high", quiet: false, claim, default);

        Assert.True(report.Moved);
        Assert.Equal("In Progress -> In Review", report.Outcome);
        Assert.Equal("s-1", report.SessionId);
        Assert.Equal(1.5m, report.Cost);

        var billed = h.Wire.To("POST", "/api/hatch/issues/AER-1/work-log")[0].Read<WorkLogEntryRequest>();
        Assert.Equal("s-1", billed.SessionId);
        Assert.Equal("Did a thing", billed.Title);
        Assert.Equal("In detail.", billed.Summary);
        Assert.Equal(12, billed.Turns);
        Assert.NotEqual(default, billed.StartedAt);

        // The board read carries the runner's own token, so its own lease does
        // not fold its own dispatch.
        Assert.Contains($"heldToken={token}", h.Wire.To("GET", "/api/hatch/work/AER-1")[0].Query,
            StringComparison.OrdinalIgnoreCase);

        await claim.ReleaseAsync();
    }

    [Fact]
    public async Task A_ticket_that_did_not_move_is_flagged_where_a_person_will_see_it()
    {
        using var h = new Harness();
        var (claim, _) = await HoldingAsync(h, "AER-1", Guid.NewGuid());

        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim/heartbeat", HttpStatusCode.NoContent);
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/work-log", Fixtures.WorkLogRow());
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1"));
        h.Wire.Json("GET", "/api/hatch/issues/AER-1/questions", Array.Empty<QuestionDto>());
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/comments",
            new CommentDto(1, "aerie-hatch", "…", "comment", null, null, DateTimeOffset.UnixEpoch));

        var report = await h.Runtime.Increment().RunAsync(
            Fixtures.Work("AER-1"), h.Root, "opus", "high", quiet: false, claim, default);

        Assert.True(report.Stalled);
        Assert.Equal("flagged", report.Flag);

        // A comment naming the session, and a question, which is what actually
        // stops the next pass spending the same money the same way.
        var written = h.Wire.To("POST", "/api/hatch/issues/AER-1/comments");
        Assert.Equal(2, written.Count);
        Assert.Contains("claude --resume s-1", written[0].Read<CommentCreateRequest>().Body, StringComparison.Ordinal);
        Assert.Equal("question", written[1].Read<CommentCreateRequest>().Kind);

        await claim.ReleaseAsync();
    }

    [Fact]
    public async Task A_ticket_already_waiting_on_a_question_gets_the_comment_and_not_a_second_question()
    {
        using var h = new Harness();
        var (claim, _) = await HoldingAsync(h, "AER-1", Guid.NewGuid());

        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim/heartbeat", HttpStatusCode.NoContent);
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/work-log", Fixtures.WorkLogRow());
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1"));
        h.Wire.Json("GET", "/api/hatch/issues/AER-1/questions", new[] { Fixtures.Question(42) });
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/comments",
            new CommentDto(1, "aerie-hatch", "…", "comment", null, null, DateTimeOffset.UnixEpoch));

        var report = await h.Runtime.Increment().RunAsync(
            Fixtures.Work("AER-1"), h.Root, "opus", "high", quiet: false, claim, default);

        Assert.Equal("waiting on a question", report.Flag);
        Assert.Single(h.Wire.To("POST", "/api/hatch/issues/AER-1/comments"));

        // And the question the session asked on its way out is printed, because
        // it is the one thing in an unattended run's output somebody has to act
        // on.
        Assert.Equal(1, report.Asked);
        Assert.Contains(h.Say.Said, l => l.Contains("asked 1 question(s)", StringComparison.Ordinal));

        await claim.ReleaseAsync();
    }

    [Fact]
    public async Task A_refused_heartbeat_stops_the_session_and_writes_nothing_past_the_work_log()
    {
        using var h = new Harness();
        var (claim, _) = await HoldingAsync(h, "AER-1", Guid.NewGuid());

        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim/heartbeat", HttpStatusCode.Conflict,
            "\"this claim was taken over\"");
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/work-log", Fixtures.WorkLogRow());
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1"));
        h.Wire.Json("GET", "/api/hatch/issues/AER-1/questions", Array.Empty<QuestionDto>());

        h.Sessions.Behaviour = FakeSessions.UntilStopped();

        var report = await h.Runtime.Increment().RunAsync(
            Fixtures.Work("AER-1"), h.Root, "opus", "high", quiet: false, claim, default);

        Assert.True(report.LostLease);
        Assert.Contains("the claim was taken", report.Flag!, StringComparison.Ordinal);

        // The session was stopped rather than left spending money on a ticket
        // this runner no longer holds.
        Assert.Equal(143, report.ExitCode);

        // A stall comment here would flag another runner's increment as ours,
        // and the ticket did not move because we stopped.
        Assert.Empty(h.Wire.To("POST", "/api/hatch/issues/AER-1/comments"));

        // The meter reading goes up either way: money was spent on that ticket,
        // and a bill is not a claim to have done the work. (This run was killed
        // before it could report, so there is nothing to post - and that too is
        // said out loud rather than posted as a row of zeros.)
        Assert.Contains(h.Say.Complained, l => l.Contains("no work log entry", StringComparison.Ordinal));

        await claim.ReleaseAsync();
    }

    [Fact]
    public async Task A_run_that_reported_its_bill_before_the_lease_went_still_posts_it()
    {
        using var h = new Harness();
        var (claim, _) = await HoldingAsync(h, "AER-1", Guid.NewGuid());

        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim/heartbeat", HttpStatusCode.Conflict,
            "\"this claim has expired\"");
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/work-log", Fixtures.WorkLogRow());
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1"));
        h.Wire.Json("GET", "/api/hatch/issues/AER-1/questions", Array.Empty<QuestionDto>());

        h.Sessions.Behaviour = async (_, onLine, ct) =>
        {
            onLine?.Invoke(Fixtures.Init());
            onLine?.Invoke(Fixtures.Result(said: "```work-log\nGot most of the way\n\nAnd then stopped.\n```"));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                return new SessionResult(143, "");
            }

            return new SessionResult(0, "");
        };

        var report = await h.Runtime.Increment().RunAsync(
            Fixtures.Work("AER-1"), h.Root, "opus", "high", quiet: false, claim, default);

        Assert.True(report.LostLease);
        Assert.Single(h.Wire.To("POST", "/api/hatch/issues/AER-1/work-log"));
        Assert.Equal("Got most of the way",
            h.Wire.To("POST", "/api/hatch/issues/AER-1/work-log")[0].Read<WorkLogEntryRequest>().Title);

        // The board read after a lost lease presents no token: this runner is no
        // longer the holder, and asking as one would be a lie about a lease.
        Assert.DoesNotContain("heldToken", h.Wire.To("GET", "/api/hatch/work/AER-1")[0].Query,
            StringComparison.OrdinalIgnoreCase);

        await claim.ReleaseAsync();
    }

    [Fact]
    public async Task The_line_the_session_is_inside_of_reaches_the_board()
    {
        using var h = new Harness();
        var (claim, _) = await HoldingAsync(h, "AER-1", Guid.NewGuid());

        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim/heartbeat", HttpStatusCode.NoContent);
        h.Wire.Json("POST", "/api/hatch/issues/AER-1/work-log", Fixtures.WorkLogRow());
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1", from: "In Review"));
        h.Wire.Json("GET", "/api/hatch/issues/AER-1/questions", Array.Empty<QuestionDto>());

        List<string?> Carried() => h.Wire.To("POST", "/api/hatch/issues/AER-1/claim/heartbeat")
            .Select(c => c.Read<ClaimHeartbeatRequest>().Chatter)
            .Where(c => c is not null)
            .ToList();

        static bool Says(List<string?> lines) =>
            lines.Any(c => c!.Contains("make test-api", StringComparison.Ordinal));

        h.Sessions.Behaviour = async (_, onLine, ct) =>
        {
            onLine?.Invoke(Fixtures.Init());
            onLine?.Invoke(Fixtures.ToolUse("Bash", "make test-api"));

            // Inside the tool use until a heartbeat has carried it, rather than
            // for a span long enough that one usually has - Harness.Eventually's
            // rule, which this test is the one place that was not keeping. The
            // beat is 25ms and the old wait was eight of them, so a loaded
            // machine that missed the window failed a test about whether the
            // line reaches the board at all. Bounded and not asserted here: the
            // assertion after the run is what judges it, and a failure there
            // reads as the missing line rather than as a crashed session.
            var waited = Stopwatch.StartNew();
            try
            {
                while (!Says(Carried()) && waited.ElapsedMilliseconds < 5_000)
                {
                    await Task.Delay(Harness.Beat, ct);
                }
            }
            catch (OperationCanceledException)
            {
                return new SessionResult(143, "");
            }

            onLine?.Invoke(Fixtures.Result());
            return new SessionResult(0, "");
        };

        await h.Runtime.Increment().RunAsync(
            Fixtures.Work("AER-1"), h.Root, "opus", "high", quiet: false, claim, default);

        Assert.Contains(Carried(), c => c!.Contains("make test-api", StringComparison.Ordinal));

        await claim.ReleaseAsync();
    }

    [Fact]
    public async Task A_run_that_never_said_what_it_spent_posts_no_row_of_zeros()
    {
        using var h = new Harness();
        var (claim, _) = await HoldingAsync(h, "AER-1", Guid.NewGuid());

        h.Wire.Reply("POST", "/api/hatch/issues/AER-1/claim/heartbeat", HttpStatusCode.NoContent);
        h.Wire.Json("GET", "/api/hatch/work/AER-1", Fixtures.Work("AER-1", from: "In Review"));
        h.Wire.Json("GET", "/api/hatch/issues/AER-1/questions", Array.Empty<QuestionDto>());

        h.Sessions.Behaviour = (_, onLine, _) =>
        {
            onLine?.Invoke(Fixtures.Init());
            return Task.FromResult(new SessionResult(1, ""));
        };

        await h.Runtime.Increment().RunAsync(
            Fixtures.Work("AER-1"), h.Root, "opus", "high", quiet: false, claim, default);

        Assert.Empty(h.Wire.To("POST", "/api/hatch/issues/AER-1/work-log"));
        Assert.Contains(h.Say.Complained, l => l.Contains("no work log entry", StringComparison.Ordinal));

        await claim.ReleaseAsync();
    }
}
