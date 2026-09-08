namespace Aerie.Hatch;

/// <summary>
/// "There is nothing an agent may move", said properly - and the one place that
/// tells a finished board from a jammed one.
/// </summary>
/// <remarks>
/// Three situations look identical from here and are not the same situation, so
/// each is named. Nothing is left on the dispatcher's path at all: the work has
/// run out. Everything on it was folded: the board is jammed, and the reasons
/// and their counts say on what. And questions are waiting on a person, which is
/// true of a subtree rather than of the dispatcher's path and so is said last
/// and separately.
/// </remarks>
public sealed class Idle(Board board, Terminal say)
{
    public async Task ReportAsync(
        string? under, IReadOnlyList<QueueEntryDto>? queue, int offsetMinutes, CancellationToken ct)
    {
        say.Line(under is { Length: > 0 }
            ? $"hatch: nothing under {under} is an agent's to move"
            : "hatch: nothing on the board is an agent's to move");

        // The scan is the caller's if it has one - a pass already made it - and
        // read here if not, so `work` on its own says as much as the loop does.
        if (queue is null)
        {
            try
            {
                queue = await board.QueueAsync(under, offsetMinutes, ct);
            }
            catch (HatchException)
            {
                // A count that did not arrive is not a count, and this is said
                // on the way past something else.
                queue = null;
            }
        }

        if (queue is not null)
        {
            if (queue.Count == 0)
            {
                // A finished board. Nothing the dispatcher looks at at all, as
                // opposed to a column of cards it looked at and could not take.
                say.Line("hatch:   nothing is on the dispatcher's path - what is left is in a terminal column");
            }
            else if (Digest.Of(queue) is { Count: > 0 } digest)
            {
                say.Line($"hatch:   {queue.Count} issue(s) were on the dispatcher's path, and every one was folded:");
                say.Lines(digest);
                say.Line("hatch:   ./scripts/hatch.sh queue names them one by one");
            }
        }

        // Scoped to match the sentence above it: an evening pointed at one epic
        // is not helped by a count of every question in the house. Counted over
        // the whole subtree rather than over the dispatcher's path, which is a
        // different and still useful number - a question on a card in a terminal
        // column is still a question somebody owes an answer to.
        var waiting = 0;
        try
        {
            waiting = under is { Length: > 0 }
                ? (await board.UnderAsync(under, ct)).Sum(i => i.OpenQuestions)
                : (await board.QuestionsAsync(null, open: true, ct)).Count;
        }
        catch (HatchException)
        {
            // As above: a loop must not end on a count that did not arrive.
        }

        if (waiting > 0)
            say.Line($"hatch: {waiting} question(s){(under is { Length: > 0 } ? $" under {under}" : "")} "
                     + "are waiting on you - ./scripts/hatch.sh answer");
    }
}
