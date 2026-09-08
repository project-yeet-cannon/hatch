namespace Aerie.Hatch;

/// <summary>
/// One increment, on the next thing due or on a named ticket.
/// </summary>
/// <remarks>
/// The claim is taken here and let go of in a <c>finally</c>, so that every way
/// out of this command goes through the same door: a finished session, a failed
/// one, one that would not start, and a Ctrl-C - which arrives as a cancelled
/// token and unwinds through the same block.
/// </remarks>
public sealed class WorkCommand(Runtime runtime)
{
    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? key = null, under = null, model = null, effort = null;
        var dry = false;
        var attach = false;
        var quiet = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length: model = args[++i]; break;
                case "--effort" when i + 1 < args.Length: effort = args[++i]; break;
                case "--under" when i + 1 < args.Length: under = args[++i]; break;
                case "--dry-run": dry = true; break;
                case "--quiet": quiet = true; break;
                case "-i" or "--interactive": attach = true; break;
                case var flag when flag.StartsWith('-'):
                    runtime.Say.Complain($"hatch: work does not take {flag}");
                    return 1;
                default: key = args[i]; break;
            }
        }

        // Two different asks: a bare key is "this ticket", --under is "whatever
        // is next below this one". Silently preferring either would be a run
        // spent on a ticket nobody named, so both together is a refusal.
        if (key is { Length: > 0 } && under is { Length: > 0 })
        {
            runtime.Say.Complain(
                "hatch: work takes a key or --under, not both - one names the ticket, the other names where to look for it");
            return 1;
        }

        return dry
            ? await DryRunAsync(key, under, model, effort, ct)
            : await SpendAsync(key, under, model, effort, attach, quiet, ct);
    }

    /// <summary>
    /// The prompt, and nothing else. Claims nothing, because it spawns nothing -
    /// and reads <c>work/next</c> rather than walking the claim, since a dry run
    /// that took a lease would be exactly the thing it is a dry run of.
    /// </summary>
    private async Task<int> DryRunAsync(string? key, string? under, string? model, string? effort, CancellationToken ct)
    {
        WorkDto? work;
        try
        {
            work = key is { Length: > 0 }
                ? await runtime.Board.WorkAsync(key, null, ct)
                : await runtime.Board.NextAsync(under, runtime.OffsetMinutes, ct);
        }
        catch (HatchException e)
        {
            runtime.Say.Complain(e.Message);
            return 1;
        }

        if (work is null)
        {
            await runtime.Idle().ReportAsync(under, null, runtime.OffsetMinutes, ct);
            return 2;
        }

        if (Refuse(work)) return 2;

        model ??= work.Playbook?.Model ?? "";
        effort ??= work.Playbook?.Effort ?? "";

        runtime.Say.Line($"# {work.Issue.Key} {work.FromStatus.Name} -> {work.ToStatus?.Name}");
        runtime.Say.Line($"# model {model}, effort {effort}");
        if (Prompt.OverrideLine(work, model, effort) is { } chose) runtime.Say.Line($"# {chose}");
        runtime.Say.Line("");
        runtime.Say.Lines(Prompt.Compose(work).Split('\n'));
        return 0;
    }

    private async Task<int> SpendAsync(
        string? key, string? under, string? model, string? effort, bool attach, bool quiet, CancellationToken ct)
    {
        if (!ClaudeSessionRunner.TryFind(runtime.Settings.ClaudeBin, out var bin, out var missing))
        {
            runtime.Say.Complain(missing);
            return 1;
        }

        WorkDto work;
        Claim claim;

        if (key is { Length: > 0 })
        {
            // A live claim held by somebody else comes back as `blocked`
            // carrying the server's sentence, so a ticket another runner is
            // working refuses here and nothing is spawned.
            WorkDto? named;
            try
            {
                named = await runtime.Board.WorkAsync(key, null, ct);
            }
            catch (HatchException e)
            {
                runtime.Say.Complain(e.Message);
                return 1;
            }

            if (named is null)
            {
                runtime.Say.Complain($"hatch: {key} - there is nothing to do here");
                return 2;
            }

            if (Refuse(named)) return 2;

            // And the race between that read and this take, which the server
            // decides and this only reports.
            var (taken, refused) = await Claim.TakeAsync(
                runtime.Board.Client, key, runtime.RunnerName, ct, runtime.Heartbeat);
            if (taken is null)
            {
                runtime.Say.Complain($"hatch: {key} - {refused?.Sentence ?? "the claim was refused"}");
                return 2;
            }

            (work, claim) = (named, taken);
        }
        else
        {
            // The same walk the loop uses, so one rule picks a ticket wherever
            // a ticket is picked.
            var picked = await runtime.Picker().PickAsync(under, runtime.OffsetMinutes, ct, runtime.Heartbeat);

            switch (picked.Outcome)
            {
                case Pick.Idle:
                    await runtime.Idle().ReportAsync(under, picked.Queue, runtime.OffsetMinutes, ct);
                    return 2;

                case Pick.Busy:
                    runtime.Say.Lines(Picker.BusyReport(under, picked.Busy));
                    return 2;

                case Pick.Unreadable:
                    return 1;
            }

            (work, claim) = (picked.Work!, picked.Claim!);
        }

        try
        {
            // What the server decided, unless a flag says otherwise. The
            // playbook's model is already the effective value - an issue
            // carrying its own has had it folded in there - so a flag beats an
            // override for free, and typing one is naming a value for this run.
            model ??= work.Playbook?.Model ?? "";
            effort ??= work.Playbook?.Effort ?? "";

            if (attach) return await AttachAsync(work, bin, model, effort, claim, ct);

            // Zero for an increment that happened, whatever the session exited
            // with: the report is where "it went badly" is said, and a shell
            // that treated a hard ticket as a broken command would be one more
            // thing an operator has to work around.
            await runtime.Increment().RunAsync(work, bin, runtime.Root, model, effort, quiet, claim, ct);
            return 0;
        }
        finally
        {
            await claim.ReleaseAsync();
        }
    }

    /// <summary>
    /// The same ticket, the same playbook, the same budget, in a session
    /// somebody is sitting in front of - claimed for as long as it runs, and
    /// released when it ends.
    /// </summary>
    /// <remarks>
    /// Its lost-lease sentinel is written and never acted on: there is a person
    /// in front of that terminal, and killing their session out from under them
    /// is not the heartbeat's call.
    /// </remarks>
    private async Task<int> AttachAsync(
        WorkDto work, string bin, string model, string effort, Claim claim, CancellationToken ct)
    {
        runtime.Say.Line($"hatch: {work.Issue.Key} [{work.Issue.Type}] {work.Issue.Title}");
        runtime.Say.Line($"hatch: {model}, effort {effort}, {work.FromStatus.Name} -> {work.ToStatus?.Name}");
        if (Prompt.OverrideLine(work, model, effort) is { } chose) runtime.Say.Line($"hatch:   {chose}");
        runtime.Say.Line("");

        return await runtime.Sessions.AttachAsync(
            new SessionRequest(bin, runtime.Root, model, effort, Prompt.Compose(work), Quiet: false), ct);
    }

    /// <summary>
    /// Whether the dispatch says no agent should be spawned - and if so, why, in
    /// the server's own sentence.
    /// </summary>
    /// <remarks>
    /// A refusal that names a question prints the question. Being told a ticket
    /// is blocked and then having to go and ask what by is two round trips for
    /// something already in hand.
    /// </remarks>
    private bool Refuse(WorkDto work)
    {
        if (work.Blocked is not { Length: > 0 } why) return false;

        runtime.Say.Complain($"hatch: {work.Issue.Key} - {why}");

        var open = work.Questions.Where(q => q.Answers.Count == 0).ToList();
        if (open.Count > 0)
        {
            runtime.Say.Complain("");
            foreach (var line in Questions.Draw(open)) runtime.Say.Complain(line);
            runtime.Say.Complain($"  ./scripts/hatch.sh answer {work.Issue.Key}");
            runtime.Say.Complain($"  {runtime.Board.Client.Origin}/issues/{work.Issue.Key}");
        }

        return true;
    }
}
