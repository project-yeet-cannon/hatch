using System.Globalization;

namespace Aerie.Hatch;

/// <summary>
/// The tally, accumulated as the loop goes rather than reconstructed at the end
/// - because an interrupted run has to be able to print it too, and an
/// interrupted run is exactly the one with nothing left to reconstruct from.
/// </summary>
public sealed class Tally
{
    private readonly TimeProvider _clock;
    private readonly List<string> _moved;
    private readonly List<string> _stalled;
    private readonly List<string> _failed = [];
    private readonly DateTimeOffset _started;

    /// <summary>
    /// A fresh night, or one already part-spent - <paramref name="carried"/>
    /// being what the incarnation before a restart handed forward. Everything a
    /// bound is measured against is seeded from it, which is what stops a
    /// restart from being a way to start the budget over.
    /// </summary>
    public Tally(TimeProvider clock, NightState? carried = null)
    {
        _clock = clock;
        _moved = [.. carried?.Moved ?? []];
        _stalled = [.. carried?.Stalled ?? []];

        // The night's start and not this process's, so the elapsed time in the
        // morning covers the whole of it. A state file with no start in it is
        // taken as starting now rather than in the year zero.
        _started = carried is { Started.Year: > 1 } ? carried.Started : clock.GetUtcNow();

        Runs = carried?.Runs ?? 0;
        Spent = carried?.Spent ?? 0m;
        Fails = carried?.Fails ?? 0;
        Restarts = carried?.Restarts ?? 0;
    }

    public int Runs { get; private set; }
    public decimal Spent { get; private set; }

    /// <summary>Increments that exited non-zero, in a row.</summary>
    public int Fails { get; private set; }

    /// <summary>How many times the loop has come back as a newer version of itself.</summary>
    public int Restarts { get; }

    /// <summary>The sentence naming what ended the run.</summary>
    public string? StopWhy { get; set; }

    // Everything the loop has to stop for, and none of them set by default. An
    // unattended run that stopped for a reason nobody asked for would be a run
    // somebody has to check on, which is the thing being built away from.
    public int? MaxRuns { get; init; }
    public decimal? MaxSpend { get; init; }
    public string? Until { get; init; }
    public DateTimeOffset? UntilAt { get; init; }
    public string? StopFile { get; init; }

    /// <summary>
    /// Whether this run is over, and why - asked before every increment and
    /// through every wait. One method, because "every exit path" is not
    /// something several call sites can promise between them.
    /// </summary>
    public bool ShouldStop()
    {
        StopWhy = null;

        // First, because it is the one that says something is wrong rather than
        // something is finished.
        if (Fails >= 3)
        {
            StopWhy = $"three increments in a row failed: {string.Join(", ", _failed)}";
            return true;
        }

        // A file, so that stopping a loop needs nothing but a shell and a path -
        // no pid to find, no signal to send, and nothing that could land in the
        // middle of a push. The increment in flight finishes first; this is only
        // ever read between them.
        if (StopFile is { Length: > 0 } stop && Path.Exists(stop))
        {
            StopWhy = $"{stop} exists";
            return true;
        }

        if (MaxRuns is { } cap && Runs >= cap)
        {
            StopWhy = $"--max-runs {cap} reached";
            return true;
        }

        if (MaxSpend is { } budget && Spent >= budget)
        {
            StopWhy = $"--max-spend {budget.ToString(CultureInfo.InvariantCulture)} reached at ${Format.Money(Spent)}";
            return true;
        }

        if (UntilAt is { } hour && _clock.GetUtcNow() >= hour)
        {
            StopWhy = $"--until {Until} has come";
            return true;
        }

        return false;
    }

    /// <summary>One increment, added to the run's account.</summary>
    public void Record(IncrementReport report)
    {
        Runs++;
        if (report.Cost is { } cost) Spent += cost;

        // Two lists rather than one, because they are two different mornings:
        // the moved ones are what the night got done, and the stalled ones are
        // what is waiting on somebody.
        if (report.Moved) _moved.Add($"hatch:   moved    {report.Key}  {report.Outcome}");
        else _stalled.Add($"hatch:   stalled  {report.Key}  {report.Outcome}");

        // A lost lease is not a failure. It is the loop working correctly on a
        // busy board - the ticket went to somebody who was already further into
        // it - and three of them in a row must not end a night the way three
        // broken increments should. It does not reset the streak either: it says
        // nothing about whether the last increment worked.
        if (report.LostLease) return;

        // A failed increment on its own is not a reason to stop - a ticket can
        // be wrong, or a test can be flaky, and the next ticket is a different
        // question. Three in a row is something else: whatever is broken is
        // broken for every ticket, and the loop is now spending money to prove
        // it.
        if (report.ExitCode != 0)
        {
            Fails++;
            _failed.Add($"{report.Key} (exit {report.ExitCode})");
        }
        else
        {
            Fails = 0;
            _failed.Clear();
        }
    }

    /// <summary>
    /// The night so far, for the incarnation that is about to take it over -
    /// counting the restart that is being asked for, since this is only ever
    /// called on the way out to one.
    /// </summary>
    public NightState ToState() => new()
    {
        Runs = Runs,
        Spent = Spent,
        Started = _started,
        Fails = Fails,
        Restarts = Restarts + 1,
        UntilAt = UntilAt,
        Moved = _moved,
        Stalled = _stalled,
    };

    /// <summary>What the night has come to so far, in one line, on the way to a restart.</summary>
    public string SoFar() => $"{Runs} increment(s), ${Format.Money(Spent)} so far, carried forward";

    /// <summary>
    /// What the run came to. Printed on the way out and nowhere else, because
    /// the reasons a loop ends include the ones nobody wrote code for - an
    /// interrupt, a failure, a terminal closing - and those are precisely the
    /// runs whose tally somebody needs.
    /// </summary>
    public void Print(Terminal say)
    {
        var elapsed = (long)(_clock.GetUtcNow() - _started).TotalSeconds;

        // Every incarnation's, because the night is the thing somebody wanted
        // counted and a restart is an implementation detail of it. The restarts
        // are named only when there were some, so a night without one reads
        // exactly as it always did.
        var restarts = Restarts > 0 ? $", {Restarts} restart(s)" : "";

        say.Line("");
        if (StopWhy is { Length: > 0 } why) say.Line($"hatch: {why}");
        say.Line($"hatch: {Runs} increment(s) in {Format.Duration(elapsed)}, ${Format.Money(Spent)}{restarts}");
        say.Lines(_moved);
        say.Lines(_stalled);
    }
}

/// <summary>
/// Something said in full the first time, again whenever the answer changes, and
/// otherwise rarely.
/// </summary>
/// <remarks>
/// An idle loop and a busy board are both things somebody left running; either
/// should be able to say it is alive without filling a scrollback with the same
/// sentence six hundred times - and the one pass whose reasons changed, because
/// somebody answered a question at three in the morning, is exactly the one
/// nobody would find in that.
/// </remarks>
public sealed class SaidOnce
{
    private string? _digest;
    private DateTimeOffset _since;
    private DateTimeOffset _last;

    /// <summary>Whether nothing has happened since this last had something to say.</summary>
    public bool Running => _since != default;

    /// <summary>How long that has been going on.</summary>
    public TimeSpan For(DateTimeOffset now) => now - _since;

    /// <summary>Whether the whole report should go out: the first time, or because it changed.</summary>
    public bool InFull(string digest, DateTimeOffset now)
    {
        if (Running && _digest == digest) return false;

        _digest = digest;
        _last = now;
        if (!Running) _since = now;
        return true;
    }

    /// <summary>Whether it is time for the short reminder that nothing has changed.</summary>
    public bool Again(DateTimeOffset now, TimeSpan after)
    {
        if (now - _last < after) return false;

        _last = now;
        return true;
    }

    /// <summary>Something happened. The next quiet spell starts from scratch.</summary>
    public void Clear()
    {
        _digest = null;
        _since = default;
        _last = default;
    }
}

/// <summary>
/// The command this epic is named for: pick the next actionable issue, spend one
/// increment on it, and do it again.
/// </summary>
/// <remarks>
/// The loop is a program and not the model. A fresh context per ticket is
/// cheaper, and a session that has been running for six hours is one whose
/// earliest decisions nobody can audit.
/// </remarks>
public sealed class GoToWorkCommand(Runtime runtime)
{
    /// <summary>How long to wait between reminders that nothing has changed.</summary>
    private static readonly TimeSpan StillNothing = TimeSpan.FromMinutes(10);

    /// <summary>
    /// What the runner exits with to ask the supervisor in <c>hatch.sh</c> to
    /// build the new source and run it again.
    /// </summary>
    /// <remarks>
    /// 75 is <c>EX_TEMPFAIL</c> - "try again" - and collides with nothing else
    /// the runner answers with: 0 for a night that ended, 1 for a refusal, 130
    /// for an interrupt. A process cannot exec itself into a newer build, so
    /// asking to be replaced is the only move available from in here.
    /// </remarks>
    public const int RestartExitCode = 75;

    /// <summary>How long a loop runs before the backstop restarts it anyway.</summary>
    private const int RestartAfterMinutes = 30;

    /// <summary>Changed paths named on a restart before the rest are counted instead.</summary>
    private const int NamedPaths = 5;

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? key = null, under = null, until = null, stopFile = null;
        var interval = 60;
        var once = false;
        var quiet = false;
        var restartAfter = RestartAfterMinutes;
        var noRestart = false;
        int? maxRuns = null;
        decimal? maxSpend = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--under" when i + 1 < args.Length: under = args[++i]; break;
                case "--interval" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out interval) || interval < 1)
                    {
                        // Zero is refused rather than clamped: it reads as "as
                        // fast as possible" and means a board asked the same
                        // question thousands of times a minute.
                        runtime.Say.Complain("hatch: --interval takes a number of seconds, at least one");
                        return 1;
                    }

                    break;
                case "--once": once = true; break;
                case "--quiet": quiet = true; break;
                case "--max-runs" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var runs) || runs < 0)
                    {
                        runtime.Say.Complain("hatch: --max-runs takes a count");
                        return 1;
                    }

                    maxRuns = runs;
                    break;
                case "--max-spend" when i + 1 < args.Length:
                    if (!decimal.TryParse(args[++i], CultureInfo.InvariantCulture, out var spend) || spend < 0)
                    {
                        runtime.Say.Complain("hatch: --max-spend takes an amount in dollars");
                        return 1;
                    }

                    maxSpend = spend;
                    break;
                case "--until" when i + 1 < args.Length: until = args[++i]; break;
                case "--stop-file" when i + 1 < args.Length: stopFile = args[++i]; break;
                case "--restart-after" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out restartAfter) || restartAfter < 0)
                    {
                        // Refused rather than clamped, like --interval: a
                        // negative age is a loop that is always too old, which
                        // is a loop that does nothing but restart.
                        runtime.Say.Complain(
                            "hatch: --restart-after takes a number of minutes, at least one - or 0 to turn it off");
                        return 1;
                    }

                    break;
                case "--no-restart": noRestart = true; break;
                case var flag when flag.StartsWith('-'):
                    runtime.Say.Complain($"hatch: go-to-work does not take {flag}");
                    return 1;
                default: key = args[i]; break;
            }
        }

        // A key with --under is the refusal `work` makes, for the reason `work`
        // makes it. A key on its own is a refusal too, and a different one: this
        // command's question is "what is next", asked again and again, and one
        // ticket cannot be the answer to it twice.
        if (key is { Length: > 0 } && under is { Length: > 0 })
        {
            runtime.Say.Complain(
                "hatch: go-to-work takes --under or a key, not both - one names where to look, the other names the ticket");
            return 1;
        }

        if (key is { Length: > 0 })
        {
            runtime.Say.Complain(
                "hatch: go-to-work does not take a ticket - it asks the board what is next, until there is nothing.");
            runtime.Say.Complain($"hatch: one increment on {key} is  hatch work {key}");
            return 1;
        }

        // Read before anything is spawned. A wrong clock discovered at the end
        // of the night is a stop condition that never applied.
        DateTimeOffset? untilAt = null;
        if (until is { Length: > 0 })
        {
            if (AtClock(until, runtime.Clock.GetLocalNow()) is not { } hour)
            {
                runtime.Say.Complain("hatch: --until takes a wall-clock time, as HH:MM");
                return 1;
            }

            untilAt = hour;
        }

        // A stop file that is already there would end the loop before its first
        // increment, silently, and look exactly like a board with nothing on it.
        if (stopFile is { Length: > 0 } && Path.Exists(stopFile))
        {
            runtime.Say.Complain(
                $"hatch: {stopFile} already exists - that is the stop signal, so nothing would run. Remove it, or name another path.");
            return 1;
        }

        using var held = LoopLock.Take(runtime.Root, runtime.TempDirectory, out var refusal);
        if (held is null)
        {
            runtime.Say.Complain(refusal);
            return 1;
        }

        // What the incarnation before a restart handed over, if this is one.
        var carried = NightState.Read(runtime.NightStatePath);

        var tally = new Tally(runtime.Clock, carried)
        {
            MaxRuns = maxRuns,
            MaxSpend = maxSpend,
            Until = until,
            // Carried rather than recomputed, and it is the one bound that would
            // otherwise be wrong: `--until 23:59` typed at 23:58 and restarted at
            // 00:01 re-reads as 23:59 tomorrow, and adds a day to the night.
            UntilAt = carried?.UntilAt ?? untilAt,
            StopFile = stopFile,
        };

        var restart = Restarts.Armed(runtime, noRestart, once, restartAfter);
        var restarting = false;

        try
        {
            restarting = await LoopAsync(under, quiet, once, interval, tally, restart, ct);

            // A restart that could not hand the night's totals over would be a
            // fresh night: the budget back to nothing, the streak cleared, the
            // hour re-read. Better the version that is running carries on to its
            // own end than a new one starts with no bounds on it.
            if (restarting && !tally.ToState().Write(runtime.NightStatePath))
            {
                runtime.Say.Complain(
                    $"hatch: could not write the night's totals to {runtime.NightStatePath} - not restarting, since the next one would start the budget over");
                tally.StopWhy = "the night's totals could not be handed forward";
                restarting = false;
            }
        }
        catch (OperationCanceledException)
        {
            // A signal caught while an increment was in flight. The claim has
            // already gone back through the pass's own way out; what is left is
            // to say why the night ended, which is the whole reason the tally is
            // printed from here and not from the loop.
            tally.StopWhy ??= "interrupted";
        }
        finally
        {
            // A restart is the middle of a night and not the end of one: the
            // tally goes into the state file for whoever comes back, and is
            // printed once, by whichever incarnation is the last.
            if (!restarting)
            {
                NightState.Forget(runtime.NightStatePath);
                tally.Print(runtime.Say);
            }
        }

        return restarting ? RestartExitCode : 0;
    }

    /// <summary>
    /// Whether this loop may come back as a newer version of itself, when it
    /// should give up waiting for a reason to, and what its own source looked
    /// like when it started.
    /// </summary>
    /// <remarks>
    /// The baseline is taken once, at startup, and deliberately not carried
    /// across a restart. That is what makes a failed rebuild safe: an
    /// incarnation running the old code with the new source already on disk has
    /// the new source as <em>its</em> baseline, and will not ask again for the
    /// same change.
    /// </remarks>
    private sealed record Restarts(bool On, TimeSpan? After, SelfPrint Baseline, DateTimeOffset Born)
    {
        public static Restarts Armed(Runtime runtime, bool noRestart, bool once, int afterMinutes)
        {
            // Three ways of being off, and the third is not a flag: no state
            // path means nobody is supervising this process, so exiting 75 would
            // simply end the night. A runner started by hand is today's loop.
            var on = !noRestart && !once && runtime.NightStatePath is { Length: > 0 };

            return new Restarts(
                On: on,
                After: afterMinutes > 0 ? TimeSpan.FromMinutes(afterMinutes) : null,
                Baseline: on ? runtime.Self().Take() : SelfPrint.Nothing,
                Born: runtime.Clock.GetUtcNow());
        }

        /// <summary>Whether this incarnation - not the night - has been up long enough.</summary>
        public bool TooOld(DateTimeOffset now) => On && After is { } age && now - Born >= age;
    }

    /// <summary>Answers whether the loop is asking to come back as a newer version of itself.</summary>
    private async Task<bool> LoopAsync(
        string? under, bool quiet, bool once, int interval, Tally tally, Restarts restart, CancellationToken ct)
    {
        var idle = new SaidOnce();
        var busy = new SaidOnce();

        if (!runtime.Sessions.CanSpawn(out var missing))
        {
            runtime.Say.Complain(missing);
            tally.StopWhy = "there is no claude CLI to spawn";
            return false;
        }

        while (!ct.IsCancellationRequested)
        {
            if (tally.ShouldStop()) return false;

            // The backstop, read here because here is where no claim is held. An
            // idle loop never resets - by design, since a fetch every interval
            // all night against a remote with nothing to say is a fetch for
            // nothing - so it never sees a change, and age is what covers it.
            // The fresh process re-reads scripts/.env, re-resolves the runner,
            // and the first pass with work in it resets and sees the rest.
            var now = runtime.Clock.GetUtcNow();
            if (restart.TooOld(now))
            {
                runtime.Say.Line("");
                runtime.Say.Line(
                    $"hatch: this loop has been running {Format.Duration((long)(now - restart.Born).TotalSeconds)} - restarting to pick up anything that landed");
                runtime.Say.Line($"hatch:   {tally.SoFar()}");
                return true;
            }

            var pass = await PassAsync(under, quiet, tally, idle, busy, interval, once, restart, ct);
            if (pass == Pass.Fatal) return false;
            if (pass == Pass.Restarting) return true;

            // `--once` is the loop's own dry run against a board that is not a
            // fixture: one pass, whatever it found, and out.
            if (once)
            {
                tally.StopWhy = "--once, and the pass is done";
                return false;
            }

            // An increment that ran is followed by the next one immediately. The
            // interval is what to do when there was nothing to do.
            if (pass == Pass.Worked) continue;
            if (!await NapAsync(interval, tally, ct)) return false;
        }

        if (ct.IsCancellationRequested) tally.StopWhy ??= "interrupted";
        return false;
    }

    private enum Pass
    {
        Worked,
        Waited,
        Fatal,

        /// <summary>
        /// The loop's own source changed under it. Nothing was spawned, and the
        /// claim goes back on the way out - a restart holds no ticket.
        /// </summary>
        Restarting,
    }

    /// <summary>
    /// One pass of the board: everything it folds past and why, and then the one
    /// thing it does about the rest.
    /// </summary>
    /// <remarks>
    /// The claim is taken before the workspace is reset and let go of in a
    /// <c>finally</c>, so every way out of a pass - a finished increment, a
    /// session that would not start, a tree that would not reset, an interrupt -
    /// gives the ticket back.
    /// </remarks>
    private async Task<Pass> PassAsync(
        string? under, bool quiet, Tally tally,
        SaidOnce idle, SaidOnce busy, int interval, bool once, Restarts restart, CancellationToken ct)
    {
        var picked = await runtime.Picker().PickAsync(under, runtime.OffsetMinutes, ct, runtime.Heartbeat);
        var now = runtime.Clock.GetUtcNow();

        switch (picked.Outcome)
        {
            case Pick.Idle:
                busy.Clear();
                await SayQuietlyAsync(idle, now, interval, once,
                    digest: string.Join('\n', Digest.Of(picked.Queue)),
                    still: "hatch: still nothing an agent may move",
                    inFull: () => runtime.Idle().ReportAsync(under, picked.Queue, runtime.OffsetMinutes, ct));
                return Pass.Waited;

            case Pick.Busy:
                idle.Clear();
                await SayQuietlyAsync(busy, now, interval, once,
                    digest: string.Join('\n', picked.Busy),
                    still: "hatch: every issue an agent could take is still being worked elsewhere",
                    inFull: () =>
                    {
                        runtime.Say.Lines(Picker.BusyReport(under, picked.Busy));
                        return Task.CompletedTask;
                    });
                return Pass.Waited;

            case Pick.Unreadable:
                // The client already said what went wrong, in a sentence. This
                // says what is going to happen about it.
                runtime.Say.Complain($"hatch: the board did not answer - asking again in {interval}s");
                return Pass.Waited;
        }

        idle.Clear();
        busy.Clear();

        var claim = picked.Claim!;
        try
        {
            // An increment is about to run, so what the pass walked past on the
            // way to it is context rather than noise.
            if (Digest.Of(picked.Queue) is { Count: > 0 } digest)
            {
                runtime.Say.Line($"hatch:   folded past {picked.Queue.Count(q => q.Blocked is not null)} issue(s) on the way here:");
                runtime.Say.Lines(digest);
            }

            foreach (var line in picked.Busy)
                runtime.Say.Line($"hatch:   another runner had{line}");

            // Here rather than at the top of the pass, and the difference is
            // only ever visible on an idle board: a reset before the board is
            // read is a fetch every interval all night, against a remote that
            // has nothing to say to a loop with nothing to run. The guarantee is
            // the same either way, because it is about the spawn and not about
            // the pass.
            switch (runtime.Workspace().Prepare())
            {
                case Reset.Never:
                    // The one condition that ends a night without an increment
                    // having failed: a tree that cannot be made current is a
                    // tree every ticket would be built wrong on, and the loop
                    // has no way to make it right.
                    tally.StopWhy = "the workspace could not be reset";
                    return Pass.Fatal;

                case Reset.Later:
                    // Nothing was spawned, nothing was spent, and the tree is
                    // where it was.
                    runtime.Say.Complain($"hatch: the workspace is not ready - trying again in {interval}s");
                    return Pass.Waited;
            }

            // The one place the change trigger can fire, because the new source
            // only exists on disk once the reset has pulled it. Inside the `try`
            // on purpose: the `finally` that gives the ticket back on every
            // other way out of a pass gives it back on this one too, so a
            // restart holds no claim and the next incarnation finds the ticket
            // free.
            if (Changed(restart) is { Count: > 0 } changed)
            {
                SayChanged(changed, tally);
                return Pass.Restarting;
            }

            runtime.Say.Line("");

            // The playbook's, and no flag reaches this: `work --model` is one
            // operator's opinion about one increment, and a loop that carried it
            // across a night would be applying it to tickets nobody looked at.
            // What the loop does carry is the ticket's own model and effort,
            // which arrive folded into the playbook already.
            var work = picked.Work!;
            var report = await runtime.Increment().RunAsync(
                work, runtime.Root, work.Playbook?.Model ?? "", work.Playbook?.Effort ?? "",
                quiet, claim, ct);

            tally.Record(report);

            runtime.Say.Line("");
            runtime.Say.Line(report.Moved
                ? $"hatch: {report.Key} moved, {report.Outcome}  ({tally.Runs} increment(s), ${Format.Money(tally.Spent)})"
                : $"hatch: {report.Key} did not move - {report.Outcome}  ({tally.Runs} increment(s), ${Format.Money(tally.Spent)})");

            return Pass.Worked;
        }
        finally
        {
            // The lease covers the bookkeeping too, and this is the door every
            // path out of a pass goes through.
            await claim.ReleaseAsync();
        }
    }

    /// <summary>
    /// What of the loop's own source is not what it was when this incarnation
    /// started. Empty when nothing changed, and when restarts are off - the
    /// print is not even read then.
    /// </summary>
    private IReadOnlyList<string> Changed(Restarts restart) =>
        restart.On ? runtime.Self().Take().ChangedFrom(restart.Baseline) : [];

    /// <summary>
    /// Which trigger fired and what it saw - because a terminal at three in the
    /// morning that goes quiet and comes back reads as a crash unless something
    /// says otherwise.
    /// </summary>
    private void SayChanged(IReadOnlyList<string> changed, Tally tally)
    {
        var named = string.Join(", ", changed.Take(NamedPaths));
        if (changed.Count > NamedPaths) named += $", and {changed.Count - NamedPaths} more";

        runtime.Say.Line("");
        runtime.Say.Line("hatch: the loop's own source changed on the trunk - restarting as the new version");
        runtime.Say.Line($"hatch:   {named}");
        runtime.Say.Line($"hatch:   {tally.SoFar()}");
    }

    private async Task SayQuietlyAsync(
        SaidOnce paced, DateTimeOffset now, int interval, bool once,
        string digest, string still, Func<Task> inFull)
    {
        var first = !paced.Running;

        if (paced.InFull(digest, now))
        {
            await inFull();

            // What the loop is going to do about it, said once. A reprint later
            // is a report about the board having changed, not a fresh
            // explanation of the interval.
            if (first && !once) runtime.Say.Line($"hatch: waiting, and asking again every {interval}s");
        }
        else if (paced.Again(now, StillNothing))
        {
            runtime.Say.Line($"{still}, {Format.Duration((long)paced.For(now).TotalSeconds)} now");
        }
    }

    /// <summary>
    /// Waiting, in slices, so that a stop file dropped during a wait is noticed
    /// then rather than an interval later, and an <c>--until</c> at three in the
    /// morning lands at three in the morning however long the interval is.
    /// </summary>
    private async Task<bool> NapAsync(int seconds, Tally tally, CancellationToken ct)
    {
        var left = seconds;
        while (left > 0)
        {
            var slice = Math.Min(5, left);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(slice), runtime.Clock, ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            left -= slice;
            if (tally.ShouldStop()) return false;
        }

        return true;
    }

    /// <summary>
    /// <c>HH:MM</c> today, or tomorrow if that hour has already gone by -
    /// somebody who says <c>--until 06:00</c> at eleven at night means the
    /// morning, and a loop that read it as "seventeen hours ago" would stop
    /// before it started.
    /// </summary>
    internal static DateTimeOffset? AtClock(string hhmm, DateTimeOffset now)
    {
        if (!TimeOnly.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hour))
            return null;

        var at = new DateTimeOffset(now.Date.Add(hour.ToTimeSpan()), now.Offset);
        return at > now ? at : at.AddDays(1);
    }
}
