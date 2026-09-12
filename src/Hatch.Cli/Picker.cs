namespace Hatch.Cli;

/// <summary>What a pass over the board came to.</summary>
public enum Pick
{
    /// <summary>A ticket is held, and the dispatch is in hand. The caller must let go of it.</summary>
    Claimed,

    /// <summary>Nothing on the board is an agent's to move.</summary>
    Idle,

    /// <summary>The board could not be read - a reason to wait, not a reason to stop.</summary>
    Unreadable,

    /// <summary>Every clear candidate is somebody else's right now.</summary>
    Busy,
}

/// <param name="Busy">One line per candidate that was taken, naming the key and who has it.</param>
public sealed record Picked(
    Pick Outcome,
    WorkDto? Work,
    Claim? Claim,
    IReadOnlyList<QueueEntryDto> Queue,
    IReadOnlyList<string> Busy);

/// <summary>
/// Which ticket this runner is going to spend an increment on, and the lease on
/// it - one walk, used wherever a ticket is picked rather than named.
/// </summary>
/// <remarks>
/// <para>Two reads, and which two matters. The <b>queue</b> applies the loop's
/// policy - the ready-date fold, the person-assignee fold - and picks the
/// candidate; the <b>named</b> read is only the payload for the candidate
/// already chosen. It has to be the named one, because <c>work/next</c> would
/// answer with the first clear row <em>as it stands now</em>, which, once
/// another runner's claim has folded the row we were about to take, is a
/// different ticket from the one we hold.</para>
///
/// <para>The claim sits between them, and that is what closes the window the old
/// pair had: a row read as clear and then taken by somebody else in the
/// meantime comes back from the second read carrying their sentence, and this
/// walks on rather than spawning into it.</para>
/// </remarks>
public sealed class Picker(Board board, string runner, Terminal say)
{
    /// <summary>
    /// How many clear rows to try before calling the board busy. A bounded walk,
    /// because a board where the first five are all being worked is a board
    /// where waiting an interval is the honest thing to do - and an unbounded
    /// one would walk a thousand-card column taking and releasing leases.
    /// </summary>
    public const int Attempts = 5;

    public async Task<Picked> PickAsync(
        string? under, int offsetMinutes, CancellationToken ct, TimeSpan? heartbeat = null)
    {
        IReadOnlyList<QueueEntryDto> queue;
        try
        {
            queue = await board.QueueAsync(under, offsetMinutes, ct);
        }
        catch (HatchException e)
        {
            say.Complain(e.Message);
            return new Picked(Pick.Unreadable, null, null, [], []);
        }

        // The rows nothing folded, in the board's order. The queue has already
        // folded past everything under a live claim held by somebody else, so
        // this is a shortlist and not the whole column.
        var clear = queue.Where(q => q.Blocked is null).Select(q => q.Issue.Key).ToList();
        if (clear.Count == 0) return new Picked(Pick.Idle, null, null, queue, []);

        var busy = new List<string>();

        foreach (var key in clear.Take(Attempts))
        {
            var (claim, refused) = await Claim.TakeAsync(board.Client, key, runner, ct, heartbeat);

            if (refused is { Held: true })
            {
                // Somebody got there between the scan and the take. Not an
                // error, and not the end of the pass: two loops that started in
                // the same second must not both idle because each saw the other
                // take the row it was about to.
                busy.Add($"  {key}  {refused.Sentence}");
                continue;
            }

            if (refused is not null || claim is null)
            {
                // A refusal that is not "somebody has it" is a board that did
                // not answer, and reporting that as a busy board would be a
                // sentence saying the opposite of what happened.
                say.Complain($"hatch: {key} - {refused?.Sentence ?? "the claim was refused"}");
                return new Picked(Pick.Unreadable, null, null, queue, busy);
            }

            WorkDto? work;
            try
            {
                work = await board.WorkAsync(key, claim.Token, ct);
            }
            catch (HatchException e)
            {
                await claim.ReleaseAsync();
                say.Complain(e.Message);
                return new Picked(Pick.Unreadable, null, null, queue, busy);
            }

            // The ticket changed under us between the two reads - somebody
            // answered a question, something landed, a dependency closed. The
            // lease goes back and the walk goes on.
            if (work is null || work.Blocked is { Length: > 0 })
            {
                await claim.ReleaseAsync();
                busy.Add($"  {key}  {work?.Blocked ?? "it left the dispatcher's path between two reads"}");
                continue;
            }

            return new Picked(Pick.Claimed, work, claim, queue, busy);
        }

        return new Picked(Pick.Busy, null, null, queue, busy);
    }

    /// <summary>
    /// The busy board, said properly - and said differently from the empty one,
    /// because they are different situations and only one of them means the
    /// night is over.
    /// </summary>
    public static IEnumerable<string> BusyReport(string? under, IReadOnlyList<string> busy)
    {
        yield return under is { Length: > 0 }
            ? $"hatch: every issue under {under} an agent could take is being worked by another runner"
            : "hatch: every issue an agent could take is being worked by another runner";

        foreach (var line in busy) yield return $"hatch: {line}";
    }
}
