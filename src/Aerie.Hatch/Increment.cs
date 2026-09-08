using System.Diagnostics;

namespace Aerie.Hatch;

/// <summary>
/// What one increment came to - read by the loop after it ends, so that a night
/// can say what happened without asking the board a second time.
/// </summary>
public sealed class IncrementReport
{
    /// <summary>The issue the increment was spent on.</summary>
    public required string Key { get; init; }

    /// <summary>The column it started in.</summary>
    public required string From { get; init; }

    /// <summary>The column the playbook was moving it to.</summary>
    public required string To { get; init; }

    /// <summary>The column it is in now, which is the only report that counts.</summary>
    public string Ended { get; set; } = "";

    /// <summary>Whether those last two differ.</summary>
    public bool Moved { get; set; }

    /// <summary>The board says it ended where it started: an increment that did nothing.</summary>
    public bool Stalled { get; set; }

    /// <summary>What was done about that, in the words the tally says it in.</summary>
    public string? Flag { get; set; }

    /// <summary>What <c>claude --resume</c> takes.</summary>
    public string? SessionId { get; set; }

    /// <summary>Dollars, out of the result event.</summary>
    public decimal? Cost { get; set; }

    /// <summary>What the CLI exited with.</summary>
    public int ExitCode { get; set; }

    /// <summary>Questions the session opened on its way out.</summary>
    public int Asked { get; set; }

    /// <summary>
    /// The lease went while the session was running, so the session was stopped.
    /// Not a failure: it is the loop working correctly on a busy board, and
    /// three of them in a row must not end a night.
    /// </summary>
    public bool LostLease { get; set; }

    /// <summary>What became of the ticket, in the phrase both the running commentary and the tally say it in.</summary>
    public string Outcome =>
        Moved ? $"{From} -> {Ended}"
        : Stalled ? $"still in \"{Ended}\"{(Flag is { Length: > 0 } ? $", {Flag}" : "")}"
        : Flag is { Length: > 0 } flag ? flag
        : "where it ended up is not known";
}

/// <summary>
/// One increment: the header, the session, and what became of the ticket.
/// </summary>
/// <remarks>
/// Everything between "here is the dispatch" and "here is what happened", which
/// is the part <c>work</c> and <c>go-to-work</c> have in common. Choosing the
/// ticket stays with the callers, because they choose it differently, and so
/// does the last word, because one of them is talking to somebody sitting there
/// and the other is writing a log nobody will read until morning.
/// </remarks>
public sealed class Increment(Board board, ISessionRunner sessions, Settings settings, Terminal say)
{
    public async Task<IncrementReport> RunAsync(
        WorkDto work, string root, string model, string effort, bool quiet,
        Claim claim, CancellationToken ct)
    {
        var report = new IncrementReport
        {
            Key = work.Issue.Key,
            From = work.FromStatus.Name,
            To = work.ToStatus?.Name ?? "?",
        };
        report.Ended = report.From;

        say.Line($"hatch: {work.Issue.Key} [{work.Issue.Type}] {work.Issue.Title}");
        say.Line($"hatch: {model}, effort {effort}, {report.From} -> {report.To}");
        if (Prompt.OverrideLine(work, model, effort) is { } chose) say.Line($"hatch:   {chose}");
        say.Line("");

        // What was open before the run, so that what the run asked can be told
        // apart from what was already sitting there. Ids rather than a count: an
        // operator who answered one question in another terminal while this ran
        // would otherwise see the arithmetic come out to zero.
        var before = work.Questions.Where(q => q.Answers.Count == 0).Select(q => q.Id).ToHashSet();

        var facts = new RunFacts();
        var started = DateTimeOffset.UtcNow;

        // The session is stopped the moment the lease goes, and only then: a
        // Ctrl-C comes down the caller's own token, which this is linked to.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        claim.OnLost = _ => stopping.Cancel();

        var result = await SpawnAsync(work, root, model, effort, quiet, facts, claim, stopping.Token);
        report.ExitCode = result.ExitCode;
        report.SessionId = facts.SessionId;
        report.Cost = facts.CostUsd;

        // Before anything is written anywhere. A lease that went means this
        // runner no longer holds the ticket, and everything below except the
        // work log is a write onto somebody else's increment.
        if (claim.Lost is { } gone)
        {
            report.LostLease = true;
            report.Flag = $"the claim was taken - {gone}";
            say.Complain($"hatch: {report.Key} - {gone}");
            say.Complain("hatch:   the session was stopped; nothing further was written there");
        }

        // Before the board is asked anything, so a row exists even when the
        // reads after it fail. Money was spent on that ticket either way, and a
        // meter reading is not a claim to have done the work.
        await PostWorkLogAsync(report.Key, facts, started, ct);

        // Where the ticket actually ended up, asked of the board rather than of
        // the session. An increment that says it did the work and leaves the
        // ticket in the column it found it in did not do the work, and this is
        // the read that can tell the difference.
        try
        {
            var later = await board.WorkAsync(report.Key, claim.Lost is null ? claim.Token : null, ct);
            report.Ended = later?.FromStatus.Name ?? report.From;
            if (report.Ended == report.From) report.Stalled = true;
            else report.Moved = true;
        }
        catch (HatchException e)
        {
            // Not knowing where it ended up is not the same as knowing it went
            // nowhere. A stall is written on the ticket in front of a person, so
            // a read that did not happen must not become one.
            say.Complain(e.Message);
            report.Flag ??= "where it ended up is not known - the board did not answer";
        }

        // Whatever the session asked for on its way out. This is the half of the
        // loop that makes asking worth doing: an unattended run's questions are
        // the one thing in its output that somebody has to act on, and they
        // would otherwise be a paragraph in the middle of a transcript nobody
        // scrolls back through.
        int? open = null;
        try
        {
            var after = await board.QuestionsAsync(report.Key, open: true, ct);
            open = after.Count;

            var asked = after.Where(q => !before.Contains(q.Id)).ToList();
            report.Asked = asked.Count;

            if (asked.Count > 0)
            {
                say.Line("");
                say.Line($"--- {report.Key} asked {asked.Count} question(s) ---");
                say.Line("");
                foreach (var line in Questions.Draw(asked)) say.Line(line);
                say.Line($"  hatch answer {report.Key}");
                say.Line($"  {board.Client.Origin}/issues/{report.Key}");
            }
        }
        catch (HatchException e)
        {
            say.Complain(e.Message);
        }

        // And if the board says nothing happened, say so on the ticket. Not for
        // an increment whose lease went: writing a stall onto a ticket another
        // runner now holds would flag their increment as ours, and the ticket
        // did not move because we stopped - which the loop already knows.
        if (report.Stalled && !report.LostLease) await FlagStallAsync(report, open, ct);

        return report;
    }

    // ---- The session ----

    private async Task<SessionResult> SpawnAsync(
        WorkDto work, string root, string model, string effort, bool quiet,
        RunFacts facts, Claim claim, CancellationToken ct)
    {
        var request = new SessionRequest(root, model, effort, Prompt.Compose(work), quiet);
        var render = new StreamRender(root, facts);

        if (quiet)
        {
            // No renderer, so the facts come from the CLI's own summary instead:
            // one object at the end carrying the session id, the cost, and the
            // last thing the session said. Nothing is carried on the claim, and
            // that is what --quiet is rather than a gap to engineer around.
            var quietly = await sessions.RunAsync(request, null, ct);
            var output = quietly.Output.Trim();

            if (output.StartsWith('{'))
            {
                // What the session said on its way out, and then the same object
                // read as the event a stream ends with - so the closing lines and
                // the facts come from one place rather than being written twice.
                //
                // Handed over whole rather than flattened onto a line: the shell
                // needed a line because its renderer read a pipe, and the text a
                // session ends with is made of newlines that matter.
                if (StreamRender.Closing(output) is { Length: > 0 } closing) say.Line(closing);
                foreach (var line in render.Read(output)) say.Line(line);
            }
            else if (output.Length > 0)
            {
                // Not JSON, so it is the CLI complaining. Whatever it said is
                // the only account of the run there is.
                say.Line(output);
            }

            return quietly;
        }

        using var pulse = new Pulse(settings.HeartbeatSeconds, say);

        return await sessions.RunAsync(request, raw =>
        {
            foreach (var line in render.Read(raw))
            {
                say.Line(line);
                pulse.Said(line);

                // Only what the run is inside of goes to the board. An empty
                // line is a blank in a transcript, and the heartbeat reads it as
                // "no change" rather than clearing a card mid-run.
                if (line.Length > 0) claim.Chatter.Line = line.Trim();
            }
        }, ct);
    }

    // ---- The meter ----

    /// <summary>
    /// What the increment just spent, on the ticket it spent it: one row per
    /// agent session, which is the only record anywhere that knows <em>which</em>
    /// ticket the money went on.
    /// </summary>
    /// <remarks>
    /// Nothing here may fail the increment. The work happened either way, and a
    /// loop must not stop because a meter did not.
    /// </remarks>
    private async Task PostWorkLogAsync(string key, RunFacts facts, DateTimeOffset started, CancellationToken ct)
    {
        // No result event arrived - an interrupted run, or a CLI that fell over
        // before it could report. Nothing is posted, because a row of zeros
        // would be a claim rather than a record.
        if (facts.Result is not { } entry)
        {
            say.Complain($"hatch: no work log entry for {key} - the run ended before it said what it had spent");
            return;
        }

        // Wall clock either side of the CLI. Deliberately not reconciled with
        // the duration the session reports: this pair says when the increment
        // occupied the machine, and DurationMs says how long it was thinking,
        // which is the smaller number and the honest answer to "how long did
        // this take".
        var row = entry with { StartedAt = started, EndedAt = DateTimeOffset.UtcNow };

        try
        {
            var written = await board.WorkLogAsync(key, row, ct);
            say.Line(written is null
                ? $"hatch: work log: written to {key}"
                : $"hatch: work log: {Format.Compact(written.TotalTokens)} tokens, "
                  + $"{Format.Spent(written.CostUsd)}, {StreamRender.Clock(written.DurationMs / 1000)}");
        }
        catch (Exception e) when (e is HatchException or OperationCanceledException)
        {
            say.Complain($"hatch: the work log entry for {key} could not be written - {e.Message}");
        }
    }

    // ---- The stall guard ----

    /// <summary>
    /// An increment that ended with the ticket in the column it found it in,
    /// said where a person will see it.
    /// </summary>
    /// <remarks>
    /// <para>This is the one failure mode of an unattended loop that is
    /// dangerous rather than merely disappointing. Nothing about the board
    /// changed, so the next pass picks the same issue, spends the same money and
    /// fails the same way - and does it all night. Every other way an increment
    /// can go badly costs one increment.</para>
    ///
    /// <para>The flag is an open question, because a question already does all
    /// three things a flag field would have to be taught: it blocks the issue
    /// from being dispatched again, it badges the card on the board, and it is
    /// the list <c>hatch.sh answer</c> walks. Answering it clears the flag,
    /// which is the right gesture - the flag means "nobody has looked at this",
    /// and answering is somebody having looked.</para>
    ///
    /// <para>Neither option is recommended, and that is not modesty.
    /// A recommendation is for a choice something knows the answer to, and the
    /// whole content of a stall is that nothing here knows why it happened.</para>
    /// </remarks>
    private async Task FlagStallAsync(IncrementReport report, int? open, CancellationToken ct)
    {
        // Nothing is written on a guess. If the questions could not be read,
        // whether this issue is already flagged is not known, and a second
        // question under the first is one more thing for somebody to answer
        // saying no more than it did.
        if (open is not { } waiting)
        {
            report.Flag = "not flagged - its questions could not be read";
            say.Complain($"hatch: {report.Key} moved nothing and could not be flagged - a later pass may offer it again");
            return;
        }

        var body =
            $"""
             An unattended increment ran here and left this issue where it found it:
             still in "{report.From}", under a playbook moving {report.From} -> {report.To}. The
             board is the report that counts, and it says nothing happened.
             """;

        body += report.SessionId is { Length: > 0 } session
            ? $"\n\nThe session it ran in is still there, with everything it did in context:\n\n    claude --resume {session}"
            : "\n\nThere is no session to resume: the run ended before it said what its id was.";

        if (report.ExitCode != 0) body += $"\n\nIt exited {report.ExitCode}.";

        say.Line("");
        if (waiting > 0)
        {
            // A question already open on the ticket is this flag, raised -
            // usually the session's own, asked on the way out by something that
            // knew why it was stopping. It blocks the next dispatch and badges
            // the same card, so all that is left to do is write down which
            // session it was.
            body += "\n\nThere is already a question open here, and that is the flag: nothing further\n"
                  + "will be dispatched at this issue until somebody answers it.";
            say.Line($"hatch: {report.Key} moved nothing, and is already waiting on a question");
        }
        else
        {
            body += "\n\nA question goes up with this comment, so nothing further will be dispatched at\n"
                  + "this issue until somebody answers it.";
            say.Line($"hatch: {report.Key} moved nothing - flagging it, and going on to the next");
        }

        say.Line("");

        // A stall is not a reason to stop, and neither is failing to write one
        // down. The comment goes on either way - it is where the session id
        // lives, and resuming the conversation is most of why a stall is worth
        // recording rather than merely counting. The question goes up only when
        // there is not one there.
        try
        {
            await board.CommentAsync(report.Key, body, ct);

            if (waiting == 0)
                await board.AskAsync(
                    report.Key,
                    $"An unattended increment left {report.Key} in \"{report.From}\" without moving it - what should happen to it now?",
                    [
                        new QuestionOptionDto("leave it",
                            "It waits for you. Nothing is dispatched at it while this question is open, so answer once you have looked - or once you have moved it somewhere the loop does not reach."),
                        new QuestionOptionDto("try again",
                            "Spend another increment on the same ticket. The next session is handed this stall, and your answer, among the decisions already made."),
                    ],
                    ct);

            report.Flag = waiting > 0 ? "waiting on a question" : "flagged";
        }
        catch (Exception e) when (e is HatchException or OperationCanceledException)
        {
            report.Flag = "not flagged - the write was refused";
            say.Complain($"hatch: {report.Key} could not be flagged - a later pass may offer it again");
        }
    }
}

/// <summary>
/// The rendered lines, plus a pulse when there are none.
/// </summary>
/// <remarks>
/// The renderer covers everything the agent says; this covers the gaps between,
/// which is where the doubt actually lives - a three-minute <c>make test-api</c>
/// produces no events at all, and silence is indistinguishable from a crash. So
/// the last thing seen is held onto and said back: "still working - Bash make
/// test-api (2m14s)" is the difference between waiting and wondering.
/// </remarks>
public sealed class Pulse : IDisposable
{
    private readonly Terminal _say;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Timer? _timer;
    private readonly Lock _gate = new();
    private TimeSpan _lastSaid;
    private string _inside = "starting up";

    public Pulse(int everySeconds, Terminal say)
    {
        _say = say;
        if (everySeconds <= 0) return;

        var every = TimeSpan.FromSeconds(everySeconds);
        _timer = new Timer(_ => Tick(every), null, every, TimeSpan.FromSeconds(1));
    }

    /// <summary>A line went out, so the silence starts again from here.</summary>
    public void Said(string line)
    {
        lock (_gate)
        {
            _lastSaid = _elapsed.Elapsed;

            // Tool calls, and nothing else - the thinking counter is already a
            // pulse, and prose is not a thing the run can be stuck inside of.
            var mark = line.IndexOf("⏺ ", StringComparison.Ordinal);
            if (mark >= 0) _inside = line[(mark + 2)..].Trim();
        }
    }

    private void Tick(TimeSpan every)
    {
        string inside;
        long seconds;

        lock (_gate)
        {
            if (_elapsed.Elapsed - _lastSaid < every) return;

            _lastSaid = _elapsed.Elapsed;
            inside = _inside;
            seconds = (long)_elapsed.Elapsed.TotalSeconds;
        }

        _say.Line($"  · still working - {inside} ({seconds / 60}m{seconds % 60:00}s)");
    }

    public void Dispose() => _timer?.Dispose();
}
