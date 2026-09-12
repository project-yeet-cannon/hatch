namespace Aerie.Hatch;

/// <summary>
/// The three reads of the board itself: what the columns hold, what is at the
/// top of one, and what a dispatch pass would look at.
/// </summary>
public sealed class BoardCommands(Cli cli)
{
    public static readonly string[] BoardUsage =
    [
        "usage: hatch board",
        "",
        "  The columns, and how many cards in each.",
    ];

    public static readonly string[] NextUsage =
    [
        "usage: hatch next [<column>]",
        "",
        "  The top workable card of a column, \"todo\" by default. A column is found",
        "  by its name on the letters and digits alone, so \"todo\" reaches \"To Do\".",
        "  Cards whose ready date has not arrived are folded past, as the board",
        "  folds them.",
    ];

    public static readonly string[] QueueUsage =
    [
        "usage: hatch queue [<ancestor key>]",
        "",
        "  Every issue a dispatch pass would look at, in the order it looks, each",
        "  with the reason it would be folded past - or the transition it is clear",
        "  for. It spawns nothing and writes nothing.",
        "",
        "  A row marked \"!\" is expedited: somebody said this one first, and the",
        "  pass considers every one of them before anything else, whatever column",
        "  each sits in.",
    ];

    /// <summary>
    /// Every column, and how many cards are on it.
    ///
    /// <para>The board's own columns first, in board order, then any deferred
    /// ones. The board page cannot draw a deferred column at all - it is a
    /// drop target, and nothing may be dropped into a siding - but a printed
    /// count is not a drop target, and a shelf holding nine tickets is worth
    /// one line rather than silence.</para>
    /// </summary>
    public async Task<int> BoardAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, BoardUsage);
        if (args.Length > 0) return Usage.Refuse(cli.Say, "board takes nothing", BoardUsage);

        var board = await cli.Board.BoardAsync(ct);
        if (board is null) return 1;

        var columns = board.Statuses
            .OrderBy(s => s.IsDeferred)
            .ToList();

        foreach (var status in columns)
        {
            var terminal = status.IsDeferred ? " (deferred)" : status.IsTerminal ? " (terminal)" : "";
            var column = board.Issues.Where(i => i.StatusId == status.Id).ToList();

            // What this command draws is a count, so this is where an expedited
            // card is marked: how many of the column are going first. Said only
            // where there are any, because a stock board has none and
            // "(0 expedited)" on every row would be five lines of nothing.
            var hurried = column.Count(i => i.Expedited);
            var first = hurried > 0 ? $"  ({hurried} expedited)" : "";

            cli.Say.Line($"{status.Name}{terminal}: {column.Count}{first}");
        }

        return 0;
    }

    /// <summary>
    /// The top workable card of a column.
    /// </summary>
    /// <remarks>
    /// The board arrives ordered by (status, expedited desc, rank, id), so "the
    /// top card" is the first survivor of the filter and no sorting happens
    /// here - which is also how an expedited card comes back from this without
    /// a line of code about it. A client with its own opinion about which
    /// ticket is next is the drift the server's ordering exists to rule out.
    /// </remarks>
    public async Task<int> NextAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, NextUsage);
        if (args.Length > 1) return Usage.Refuse(cli.Say, "next takes one column", NextUsage);

        var want = args.Length == 1 ? args[0] : "todo";

        var board = await cli.Board.BoardAsync(ct);
        if (board is null) return 1;

        if (Columns.Find(board.Statuses, want) is not { } column)
        {
            cli.Say.Complain($"hatch: no column called \"{want}\" - there is {Columns.Named(board.Statuses)}");
            return 1;
        }

        var card = board.Issues.FirstOrDefault(i => i.StatusId == column.Id && Columns.Ready(i.ReadyAt, cli.Now));
        if (card is null)
        {
            cli.Say.Complain($"hatch: nothing workable in \"{want}\"");
            return 2;
        }

        var due = card.DueAt is { Length: > 0 } by ? $"  (due {by})" : "";
        var first = card.Expedited ? "  (expedited)" : "";
        cli.Say.Line($"{card.Key}  [{card.Type}]  {card.Title}{first}{due}");
        return 0;
    }

    /// <summary>
    /// Everything a pass would look at, and what it would decide about each.
    /// </summary>
    /// <remarks>
    /// <para><c>next</c> and <c>work</c> fold past what they cannot do in
    /// silence, which is right when somebody is watching - "nothing to do" is
    /// the useful answer. Nobody is watching an unattended loop, and then the
    /// reasons are the whole point: a column and type nobody has written a
    /// playbook for reads as a finished board from the outside, and this is
    /// where it stops reading that way.</para>
    ///
    /// <para>The order is the dispatcher's, and nothing here re-sorts it. A
    /// card's line is its place on the board.</para>
    /// </remarks>
    public async Task<int> QueueAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, QueueUsage);
        if (args.Length > 1) return Usage.Refuse(cli.Say, "queue takes one ancestor key", QueueUsage);

        var under = args.Length == 1 ? args[0] : null;
        var queue = await cli.Board.QueueAsync(under, cli.OffsetMinutes, ct);

        // An empty board is a sentence and not a blank line: "there is nothing"
        // and "something went wrong and printed nothing" look identical
        // otherwise, which is the one thing a run nobody watched cannot afford
        // to be unsure about.
        if (queue.Count == 0)
        {
            cli.Say.Line(under is { Length: > 0 }
                ? $"hatch: nothing under {under} is on the dispatcher's path"
                : "hatch: nothing on the board is on the dispatcher's path");
            return 0;
        }

        foreach (var line in Draw(queue)) cli.Say.Line(line);
        return 0;
    }

    /// <summary>
    /// The rows, padded to the widest value in the answer rather than to a
    /// guessed width - status names are rows the operator renames.
    /// </summary>
    /// <remarks>
    /// The <c>!</c> in front of an expedited row is the same idea one step
    /// further: the column appears only when the answer holds one, so a board
    /// with nothing expedited prints exactly what it printed before, and a
    /// queue whose order has been reordered by somebody says which rows did it.
    /// </remarks>
    public static IReadOnlyList<string> Draw(IReadOnlyList<QueueEntryDto> queue)
    {
        var keyWidth = queue.Max(q => q.Issue.Key.Length);
        var typeWidth = queue.Max(q => q.Issue.Type.Length) + 2;
        var columnWidth = queue.Max(q => q.FromStatus.Name.Length);
        var anyFirst = queue.Any(q => q.Issue.Expedited);

        return queue.Select(q =>
                (anyFirst ? (q.Issue.Expedited ? "! " : "  ") : "")
                + q.Issue.Key.PadRight(keyWidth) + "  "
                + $"[{q.Issue.Type}]".PadRight(typeWidth) + "  "
                + q.FromStatus.Name.PadRight(columnWidth) + "  "
                + (q.Blocked is { Length: > 0 } why ? why : $"-> {q.ToStatus?.Name ?? "?"}"))
            .ToList();
    }
}
