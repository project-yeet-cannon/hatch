namespace Aerie.Hatch;

/// <summary>
/// The four things a working session does to one ticket: read it, move it,
/// write on it, and say where it is being reviewed.
/// </summary>
public sealed class IssueCommands(Cli cli)
{
    public static readonly string[] ShowUsage =
    [
        "usage: hatch show AER-12",
        "",
        "  The brief, its edges and where it is, plus every comment on it. The",
        "  description is markdown and goes to the terminal as it was written.",
    ];

    public static readonly string[] StartUsage =
    [
        "usage: hatch start AER-12",
        "",
        "  Move it to \"in progress\", so the board says what is being worked on",
        "  right now. That is the board's whole job.",
    ];

    public static readonly string[] MoveUsage =
    [
        "usage: hatch move AER-12 <column>",
        "",
        "  Into any non-terminal column, found by its name on the letters and",
        "  digits alone. A terminal column is refused: only the operator decides",
        "  that something shipped (CLAUDE.md).",
    ];

    public static readonly string[] CommentUsage =
    [
        "usage: hatch comment AER-12 \"the body\"",
        "",
        "  The ticket is where somebody looks in six months, and a comment naming",
        "  a commit is what makes that search short.",
    ];

    public static readonly string[] PrUsage =
    [
        "usage: hatch pr AER-12 [<url>|--clear]",
        "",
        "  hatch pr AER-12              where it is being reviewed, and nothing else",
        "  hatch pr AER-12 https://...  ...or say where, having opened one",
        "  hatch pr AER-12 --clear      ...or take it off the one it has",
        "",
        "  The read prints the URL alone, so `open \"$(hatch pr AER-12)\"` is the",
        "  whole of \"show me the review\".",
    ];

    /// <summary>The ticket, rendered plainly rather than cleverly.</summary>
    public async Task<int> ShowAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, ShowUsage);
        if (args.Length != 1) return Usage.Refuse(cli.Say, "show takes one issue key", ShowUsage);

        var key = args[0];
        var issue = await cli.Board.IssueAsync(key, ct);
        if (issue is null)
        {
            cli.Say.Complain($"hatch: {key} - there is nothing there");
            return 1;
        }

        var statuses = await cli.Board.StatusesAsync(ct);
        var comments = await cli.Board.CommentsAsync(key, ct);

        var column = statuses.FirstOrDefault(s => s.Id == issue.StatusId)?.Name
                     ?? issue.StatusId.ToString();

        cli.Say.Line($"{issue.Key}  [{issue.Type}]  {issue.Title}");
        cli.Say.Line($"status:   {column}");
        if (issue.ParentKey is { Length: > 0 } parent) cli.Say.Line($"parent:   {parent}");
        if (issue.ChildKeys.Count > 0) cli.Say.Line($"children: {string.Join(", ", issue.ChildKeys)}");
        if (issue.DependsOnKeys.Count > 0) cli.Say.Line($"depends:  {string.Join(", ", issue.DependsOnKeys)}");
        if (issue.DependentKeys.Count > 0) cli.Say.Line($"blocks:   {string.Join(", ", issue.DependentKeys)}");
        if (issue.ReadyAt is { Length: > 0 } ready) cli.Say.Line($"ready:    {ready}");
        if (issue.DueAt is { Length: > 0 } due) cli.Say.Line($"due:      {due}");
        cli.Say.Line("");
        cli.Say.Lines(issue.Description.ReplaceLineEndings("\n").Split('\n'));
        cli.Say.Line("");

        if (comments.Count == 0) return 0;

        cli.Say.Line($"--- {comments.Count} comment(s) ---");
        foreach (var comment in comments)
        {
            cli.Say.Line($"[{Format.Stamp(comment.CreatedAt)}] {comment.Author}:");
            cli.Say.Lines(comment.Body.ReplaceLineEndings("\n").Split('\n'));
            cli.Say.Line("");
        }

        return 0;
    }

    /// <summary><c>start</c>, which is <c>move</c> with the column already named.</summary>
    public Task<int> StartAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Task.FromResult(Usage.Print(cli.Say, StartUsage));
        if (args.Length != 1) return Task.FromResult(Usage.Refuse(cli.Say, "start takes one issue key", StartUsage));

        return MoveAsync([args[0], "in progress"], ct);
    }

    /// <summary>
    /// Into a column, by name.
    /// </summary>
    /// <remarks>
    /// A terminal column is refused here rather than left to a careful prompt,
    /// because the rule is about the tool and not about who is holding it.
    /// </remarks>
    public async Task<int> MoveAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, MoveUsage);
        if (args.Length != 2) return Usage.Refuse(cli.Say, "move takes one issue key and one column", MoveUsage);

        var (key, want) = (args[0], args[1]);

        var board = await cli.Board.BoardAsync(ct);
        if (board is null) return 1;

        if (Columns.Find(board.Statuses, want) is not { } column)
        {
            cli.Say.Complain($"hatch: no column called \"{want}\" - there is {Columns.Named(board.Statuses)}");
            return 1;
        }

        if (column.IsTerminal)
        {
            cli.Say.Complain(
                $"hatch: \"{want}\" is a terminal column - only the operator moves a ticket there (CLAUDE.md)");
            return 1;
        }

        var moved = await cli.Board.MoveAsync(key, column.Id, ct);
        cli.Say.Line($"{moved?.Key ?? key} -> {want}");
        return 0;
    }

    public async Task<int> CommentAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, CommentUsage);
        if (args.Length != 2) return Usage.Refuse(cli.Say, "comment takes one issue key and one body", CommentUsage);

        var written = await cli.Board.CommentAsync(args[0], args[1], ct);
        cli.Say.Line($"commented on {args[0]} as {written?.Author ?? "somebody"}");
        return 0;
    }

    /// <summary>
    /// Where an issue is being reviewed: read it, set it, or take it off.
    /// </summary>
    /// <remarks>
    /// The setting half is what a session that has just opened a pull request
    /// runs, so that the URL lands on the ticket instead of in a comment
    /// somebody has to find later.
    /// </remarks>
    public async Task<int> PrAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, PrUsage);
        if (args.Length is 0 or > 2) return Usage.Refuse(cli.Say, "pr takes one issue key and one url", PrUsage);

        var key = args[0];

        // No second argument is a read. Distinguished by the count rather than
        // by the value, so that an empty one is the mistake below and not a
        // silent clear.
        if (args.Length == 1)
        {
            var issue = await cli.Board.IssueAsync(key, ct);
            if (issue?.PullRequestUrl is not { Length: > 0 } found)
            {
                cli.Say.Complain($"hatch: {key} points at no pull request");
                return 2;
            }

            cli.Say.Line(found);
            return 0;
        }

        // "" is what the API reads as the clear, and an empty argument at a
        // prompt is easy to pass by accident and impossible to see afterwards.
        // So the clear is spelled out loud and an empty string is refused.
        var url = args[1];
        if (url == "--clear")
        {
            url = "";
        }
        else if (url.Length == 0)
        {
            cli.Say.Complain($"hatch: give a url, or --clear to take {key} off the one it has");
            return 1;
        }

        var patched = await cli.Board.PatchAsync(key, new IssuePatchRequest(
            Title: null, Description: null, Type: null, StatusId: null, ParentKey: null,
            ReadyAt: null, DueAt: null, PullRequestUrl: url), ct);

        cli.Say.Line(patched?.PullRequestUrl is { Length: > 0 } now
            ? now
            : $"{patched?.Key ?? key} points at no pull request");
        return 0;
    }
}
