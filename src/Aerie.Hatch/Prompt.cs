using System.Text;

namespace Aerie.Hatch;

/// <summary>
/// The whole instruction handed to a spawned session: the playbook first,
/// because it says what kind of job this is, then the ticket it is a job about.
/// </summary>
/// <remarks>
/// Composed here rather than stored whole in the database so that a playbook
/// stays a method - one row that reads sensibly for every ticket it will ever
/// be applied to.
/// </remarks>
public static class Prompt
{
    public static string Compose(WorkDto work)
    {
        var issue = work.Issue;
        var key = issue.Key;
        var to = work.ToStatus?.Name ?? "?";
        var lines = new List<string>
        {
            work.Playbook?.Prompt ?? "",
            "",
            "---",
            "",
            "## The ticket",
            "",
            $"{key}  [{issue.Type}]  {issue.Title}",
            $"moving:   {work.FromStatus.Name} -> {to}",
        };

        if (issue.ParentKey is { Length: > 0 }) lines.Add($"parent:   {issue.ParentKey}");
        if (issue.ReadyAt is { Length: > 0 }) lines.Add($"ready:    {issue.ReadyAt}");
        if (issue.DueAt is { Length: > 0 }) lines.Add($"due:      {issue.DueAt}");

        lines.Add("");
        lines.Add(issue.Description.Length > 0
            ? issue.Description
            : "_No description. That is itself worth noting on the ticket._");
        lines.Add("");

        if (work.Children.Count > 0)
        {
            lines.Add("## Its children");
            lines.Add("");
            lines.AddRange(work.Children.Select(c => $"- {c.Key}  [{c.Type}]  {c.Title}"));
            lines.Add("");
        }

        // Decisions that were asked for and given, on this ticket, before now.
        // Carried into the prompt rather than left for the agent to find in the
        // comment thread, because the one thing a session must not do is reopen
        // a question somebody has already answered.
        var answered = work.Questions.Where(q => q.Answers.Count > 0).ToList();
        if (answered.Count > 0)
        {
            lines.Add("## Decisions already made");
            lines.Add("");
            lines.Add("These were asked on this ticket and answered. They are settled: build on");
            lines.Add("them, and do not ask again.");
            lines.Add("");

            for (var i = 0; i < answered.Count; i++)
            {
                if (i > 0)
                {
                    lines.Add("");
                    lines.Add("---");
                    lines.Add("");
                }

                lines.Add($"**Asked ({answered[i].AskedBy}):** {answered[i].Body}");

                foreach (var answer in answered[i].Answers)
                {
                    lines.Add("");
                    lines.Add($"**Answered ({answer.Author}):** {answer.Body}");
                }
            }

            lines.Add("");
        }

        lines.AddRange(Tail(key, to));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// The part that is the same for every ticket: how to reach Hatch, what to
    /// do with a decision that is not the implementer's, and where the
    /// increment ends.
    /// </summary>
    private static IEnumerable<string> Tail(string key, string to) =>
    [
        "## Reaching Hatch",
        "",
        "Run these from the repository root. The key is already in the environment.",
        "",
        "```",
        $"hatch show {key}              the ticket and its comments",
        $"hatch start {key}             move it to in progress",
        $"hatch move {key} <column>     move it anywhere non-terminal",
        $"hatch comment {key} \"...\"     write on the ticket",
        $"hatch ask {key} \"...\"         ask for a decision, and stop",
        $"hatch api GET /api/hatch/issues?parentKey={key}",
        "```",
        "",
        "Filing new issues, editing descriptions and setting dates all go through",
        "`api`, and docs/hatch-planning.md documents the shapes. Read it if this",
        "increment files or reshapes work; an increment that only writes code does",
        "not need it.",
        "",
        "## When you cannot decide",
        "",
        "Some things are not yours to choose: a product call, a name that will be",
        "lived with for years, a tradeoff with no technically correct side. When you",
        "reach one, do not guess, and do not quietly pick whichever option is easiest",
        "to build.",
        "",
        "```",
        $"hatch ask {key} \"the question, in one sentence\" \\",
        "    --recommend \"The one you would take: what it means, and what it costs\" \\",
        "    --option \"The alternative: what it means, and what it costs\"",
        "```",
        "",
        "**Name the choices.** Nearly every decision worth asking about is a choice",
        "between two or three things you can already name, and each `--option` becomes",
        "something the operator presses - in the browser and at a terminal - rather",
        "than a paragraph they have to read twice and then compose a reply to. So the",
        "body is the question alone, in a sentence; the tradeoffs go inside the options",
        "they belong to; and `--recommend` is the one you would take, of which there",
        "may be one. Ask in prose only when the answer is genuinely open-ended.",
        "",
        "A label is short enough to press and reads as a decision on its own -",
        "\"child-weighted\", not \"we should weight each direct child equally\". It",
        "becomes the answer text itself, and that is what somebody reads six months",
        "later.",
        "",
        "One call per question, so each can be answered on its own. Then stop. An open",
        "question blocks this ticket from being dispatched at all, so nothing further",
        "will be spawned at it until somebody answers - and anything built past an",
        "unanswered question is built on a guess.",
        "",
        "What the repository can answer, answer by reading the repository. A question",
        "the code already settles is a round trip through a person for nothing.",
        "",
        "## Where this increment ends",
        "",
        $"{key} should be in \"{to}\" when you stop, and no further.",
        "Only the operator moves work into a terminal column.",
        "",
        "Do not edit playbooks. The API refuses it, and the refusal is deliberate:",
        "an agent that could widen its own instructions and its own budget is a loop",
        "with no end. If a playbook is wrong, say so on the ticket and stop.",
        "",
        "Last of all, say what you did, in a fenced block:",
        "",
        "```work-log",
        "A title naming what this session did",
        "",
        "The summary, under 100 words.",
        "```",
        "",
        $"That block becomes the work log row for this session on {key},",
        "beside what the session cost. A run that writes none still gets its row,",
        "marked as never having said what it did - so the block is worth the two",
        "lines it takes. Write it last, and write it once.",
    ];

    /// <summary>
    /// Which of the model and the effort the ticket chose rather than the
    /// playbook, as half a sentence - or nothing at all, which is every issue
    /// on a stock board.
    /// </summary>
    /// <remarks>
    /// The server has already folded an issue's overrides into the playbook it
    /// hands back, so the playbook's model is the value that won and there is
    /// nothing here to decide. What is left is saying where it came from, and
    /// that is decided by comparing the value being spawned on against the
    /// override the issue carries - not by tracking whether a flag was given.
    /// The comparison is derivable from what the caller already holds, it is
    /// exactly right on every unattended path, and the one case it softens - an
    /// operator typing <c>--model opus</c> at a ticket already set to
    /// <c>opus</c> - prints a sentence that is still true.
    /// </remarks>
    public static string? OverrideLine(WorkDto work, string model, string effort)
    {
        var which = new StringBuilder();

        if (work.Issue.ModelOverride is { Length: > 0 } m && m == model) which.Append("model");
        if (work.Issue.EffortOverride is { Length: > 0 } e && e == effort)
        {
            if (which.Length > 0) which.Append(" and ");
            which.Append("effort");
        }

        return which.Length == 0 ? null : $"{which} from {work.Issue.Key}, not the playbook";
    }
}
