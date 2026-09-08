namespace Aerie.Hatch;

/// <summary>
/// What an issue waits on, and what waits on it.
/// </summary>
/// <remarks>
/// <para>An edge is what serialises work - not shared parentage, which the
/// board used to guess from. A planning session that has just filed five
/// stories that must land one after another chains them here, and the loop then
/// walks the chain in order; five stories that are independent get nothing and
/// go in whatever order the board puts them in.</para>
///
/// <para>It gates one move: the one into the column where the code gets
/// written. An issue waiting on another is still broken down, still lands in
/// the backlog and is still analysed - and the edge clears only when the issue
/// it names is in a terminal column, because the point is that story two is not
/// written on story one's unmerged branch.</para>
/// </remarks>
public sealed class DependsCommand(Cli cli)
{
    public static readonly string[] DependsUsage =
    [
        "usage: hatch depends AER-12 [<key>|--remove <key>]",
        "",
        "  hatch depends AER-12                  what it waits on, and what waits on it",
        "  hatch depends AER-12 AER-11           AER-12 waits on AER-11",
        "  hatch depends AER-12 --remove AER-11  ...no longer",
        "",
        "  An edge gates one move: the one into the column where the code gets",
        "  written, and it clears when the issue it names is merged.",
    ];

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, DependsUsage);
        if (args.Length == 0) return Usage.Refuse(cli.Say, "depends takes an issue key", DependsUsage);

        var key = args[0];
        IssueDto? issue;

        // No second argument is a read, distinguished by the count rather than
        // by the value - so an empty one is the mistake below and not a silent
        // nothing. `pr` splits on the same rule and for the same reason.
        if (args.Length == 1)
        {
            issue = await cli.Board.IssueAsync(key, ct);
        }
        else if (args[1] == "--remove")
        {
            if (args.Length < 3 || args[2].Length == 0)
            {
                cli.Say.Complain($"hatch: say which issue {key} should stop waiting on");
                return 1;
            }

            issue = await cli.Board.UndependAsync(key, args[2], ct);
        }
        else if (args[1].Length > 0)
        {
            issue = await cli.Board.DependAsync(key, args[1], ct);
        }
        else
        {
            cli.Say.Complain($"hatch: say which issue {key} waits on, or --remove one");
            return 1;
        }

        if (issue is null)
        {
            cli.Say.Complain($"hatch: {key} - there is nothing there");
            return 1;
        }

        foreach (var line in Draw(issue)) cli.Say.Line(line);
        return 0;
    }

    /// <summary>
    /// Both directions, printed even when empty - "there is nothing" and
    /// "something went wrong and printed nothing" look identical otherwise.
    /// Keys alone, as <c>show</c> prints children: this is one read of the issue
    /// and no fan-out.
    /// </summary>
    public static IReadOnlyList<string> Draw(IssueDto issue) =>
    [
        issue.DependsOnKeys.Count > 0
            ? $"{issue.Key} waits on {string.Join(", ", issue.DependsOnKeys)}"
            : $"{issue.Key} waits on nothing",
        issue.DependentKeys.Count > 0
            ? $"{string.Join(", ", issue.DependentKeys)} " +
              $"{(issue.DependentKeys.Count == 1 ? "waits" : "wait")} on {issue.Key}"
            : $"nothing waits on {issue.Key}",
    ];
}
