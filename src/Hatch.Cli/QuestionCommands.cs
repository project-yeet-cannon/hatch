namespace Hatch.Cli;

/// <summary>
/// Asking for a decision, seeing what is waiting on one, and giving them.
/// </summary>
public sealed class QuestionCommands(Cli cli)
{
    public static readonly string[] AskUsage =
    [
        "usage: hatch ask AER-12 \"the question\" [--option \"Label: what it means\"]...",
        "                                      [--recommend \"Label: what it means\"]",
        "",
        "  A question that is a choice between named things should name them: each",
        "  --option becomes something the operator can press, in the web UI and here.",
        "  --recommend is an option you would take, and there may be one of those.",
        "",
        "  One question a call, so each can be answered on its own. Then stop: an",
        "  open question blocks the ticket from being dispatched at all.",
    ];

    public static readonly string[] QuestionsUsage =
    [
        "usage: hatch questions [AER-12]",
        "",
        "  Everything waiting on an answer, or just this ticket's.",
    ];

    public static readonly string[] AnswerUsage =
    [
        "usage: hatch answer [AER-12]",
        "",
        "  Every open question, one at a time, on this terminal. A number takes",
        "  that option, Enter alone leaves one open, a trailing \\ keeps typing,",
        "  and ^D stops.",
    ];

    /// <summary>
    /// What an agent calls when it hits a decision that is not its to make.
    /// </summary>
    /// <remarks>
    /// One question a call: a question is a row, and two of them in one body
    /// cannot be answered separately or counted apart. Where the decision is a
    /// choice between named things, name them - an option is something the
    /// operator presses, and a paragraph is something they have to read twice
    /// and then compose a reply to. Prose still works, for the decisions that
    /// are not a menu.
    /// </remarks>
    public async Task<int> AskAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, AskUsage);

        string? key = null, body = null;
        var options = new List<QuestionOptionDto>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--option" when i + 1 < args.Length:
                    options.Add(Option(args[++i], recommended: false));
                    break;

                case "--recommend" when i + 1 < args.Length:
                    options.Add(Option(args[++i], recommended: true));
                    break;

                case "--option" or "--recommend":
                    return Usage.Refuse(cli.Say, $"{args[i]} needs \"Label: what it means\"", AskUsage);

                case var flag when flag.StartsWith('-'):
                    return Usage.Refuse(cli.Say, $"ask does not take {flag}", AskUsage);

                default:
                    if (key is null) key = args[i];
                    else if (body is null) body = args[i];
                    else return Usage.Refuse(
                        cli.Say, "ask takes one issue and one question - put the choices in --option", AskUsage);
                    break;
            }
        }

        if (key is not { Length: > 0 } || body is not { Length: > 0 })
            return Usage.Refuse(cli.Say, "ask takes one issue and one question", AskUsage);

        var asked = await cli.Board.AskAsync(key, body, options, ct);

        var withOptions = options.Count > 0 ? $" with {options.Count} options" : "";
        cli.Say.Line($"asked question #{asked?.Id} on {key} as {asked?.Author ?? "somebody"}{withOptions}");
        cli.Say.Line("");
        cli.Say.Line($"{key} will not be dispatched again until it is answered:");
        cli.Say.Line($"  hatch answer {key}");
        cli.Say.Line($"  {cli.IssueUrl(key)}");
        return 0;
    }

    /// <summary>
    /// One <c>--option "Label: what taking it means"</c>, folded into a record.
    /// </summary>
    /// <remarks>
    /// Split on the first <c>": "</c> so that a detail may contain colons,
    /// which prose does constantly. A spec with no separator at all is a bare
    /// label - fine for a choice that explains itself, and the only shape short
    /// enough to type twice.
    /// </remarks>
    public static QuestionOptionDto Option(string spec, bool recommended)
    {
        var split = spec.IndexOf(": ", StringComparison.Ordinal);
        return split < 0
            ? new QuestionOptionDto(spec, null, recommended)
            : new QuestionOptionDto(spec[..split], spec[(split + 2)..], recommended);
    }

    public async Task<int> QuestionsAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, QuestionsUsage);
        if (args.Length > 1) return Usage.Refuse(cli.Say, "questions takes one issue key", QuestionsUsage);

        var key = args.Length == 1 ? args[0] : null;
        var open = await cli.Board.QuestionsAsync(key, open: true, ct);

        if (open.Count == 0)
        {
            cli.Say.Line($"hatch: nothing is waiting on an answer{Named(key)}");
            return 0;
        }

        cli.Say.Line($"--- {open.Count} open question(s) ---");
        cli.Say.Line("");
        cli.Say.Lines(Questions.Draw(open));
        cli.Say.Line($"answer them: hatch answer{Named(key, prefix: " ")}");
        return 0;
    }

    /// <summary>
    /// Every open question, one at a time, on this terminal.
    /// </summary>
    /// <remarks>
    /// <para>The serial loop is the point rather than a convenience. A question
    /// is a decision somebody owes, and the way to get a decision out of a
    /// person is to ask them one thing and wait for it - a list of six printed
    /// at once gets skimmed and answered in aggregate, which is how a wrong
    /// assumption gets in.</para>
    ///
    /// <para>An empty reply leaves that question open and moves on, so a session
    /// can be abandoned halfway without anybody having to answer something badly
    /// to get out of it. Every answer that is given goes to the ticket as it is
    /// typed, so a loop interrupted at question four keeps the first three.</para>
    /// </remarks>
    public async Task<int> AnswerAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(cli.Say, AnswerUsage);
        if (args.Length > 1) return Usage.Refuse(cli.Say, "answer takes one issue key", AnswerUsage);

        if (!cli.In.Interactive)
        {
            cli.Say.Complain("hatch: answer reads your replies and needs a terminal");
            return 1;
        }

        var key = args.Length == 1 ? args[0] : null;
        var open = await cli.Board.QuestionsAsync(key, open: true, ct);

        if (open.Count == 0)
        {
            cli.Say.Line($"hatch: nothing is waiting on an answer{Named(key)}");
            return 0;
        }

        cli.Say.Line($"{open.Count} open question(s). A number takes that option, Enter alone leaves one open,");
        cli.Say.Line("\\ at the end of a line keeps typing, ^D stops.");
        cli.Say.Line("");

        var answered = 0;

        for (var i = 0; i < open.Count; i++)
        {
            var question = open[i];

            cli.Say.Line($"{i + 1}/{open.Count}  {question.IssueKey}  {question.IssueTitle}");
            cli.Say.Line($"        asked by {question.AskedBy}, {Format.Stamp(question.AskedAt)}");
            cli.Say.Line("");
            foreach (var line in question.Body.ReplaceLineEndings("\n").Split('\n')) cli.Say.Line($"  {line}");
            cli.Say.Line("");

            var options = question.Options ?? [];
            if (options.Count > 0)
            {
                for (var n = 0; n < options.Count; n++)
                {
                    cli.Say.Line($"  [{n + 1}] {options[n].Label}{(options[n].Recommended ? "  (recommended)" : "")}");
                    if (options[n].Detail is not { Length: > 0 } detail) continue;
                    foreach (var line in detail.ReplaceLineEndings("\n").Split('\n')) cli.Say.Line($"      {line}");
                }

                cli.Say.Line("");
            }

            var typed = ReadReply(out var ended);

            // ^D at the prompt ends the whole session rather than this question:
            // it is the gesture for "I am done here", and treating it as an
            // empty answer would silently walk the rest of the list.
            if (ended) break;

            // A bare number that names one of the offered answers is that
            // answer, and what gets written is its label - so the thread reads
            // as a decision rather than as an index into a list nobody kept.
            var reply = Chosen(options, typed);

            if (reply.Trim().Length == 0)
            {
                cli.Say.Line("  left open");
            }
            else
            {
                await cli.Board.AnswerAsync(question.IssueKey, question.Id, reply, ct);
                answered++;
                cli.Say.Line($"  answered: {reply.ReplaceLineEndings("\n").Split('\n')[0]}");
            }

            cli.Say.Line("");
        }

        cli.Say.Line($"answered {answered} of {open.Count}");

        // Said rather than left to be remembered: answering is only half of it,
        // and the ticket does not move until somebody dispatches it again.
        if (answered > 0)
            cli.Say.Line($"the tickets that are now clear can be worked: hatch work{Named(key, prefix: " ")}");

        return 0;
    }

    /// <summary>One reply, which may be several lines where each but the last ends in a backslash.</summary>
    private string ReadReply(out bool ended)
    {
        var reply = new System.Text.StringBuilder();
        ended = false;

        while (true)
        {
            cli.In.Prompt("> ");
            if (cli.In.Line() is not { } line)
            {
                ended = true;
                cli.Say.Line("");
                return "";
            }

            if (!line.EndsWith('\\')) return reply.Append(line).ToString();

            reply.Append(line[..^1]).Append('\n');
        }
    }

    /// <summary>
    /// What a typed reply actually means, given what was offered.
    /// </summary>
    /// <remarks>
    /// A bare number in range is the option at that position, and what comes
    /// back is its label - the answer on the ticket should read as the decision
    /// it was, not as "2". Anything else comes back untouched, including a
    /// number when nothing was offered and a number out of range: those are
    /// somebody typing an answer that happens to be numeric, and second-guessing
    /// them would be worse than taking them literally.
    /// </remarks>
    public static string Chosen(IReadOnlyList<QuestionOptionDto> options, string reply)
    {
        if (options.Count == 0) return reply;
        if (reply.Length == 0 || !reply.All(char.IsAsciiDigit)) return reply;

        return int.TryParse(reply, out var n) && n >= 1 && n <= options.Count ? options[n - 1].Label : reply;
    }

    private static string Named(string? key, string prefix = " on ") =>
        key is { Length: > 0 } ? prefix + key : "";
}
