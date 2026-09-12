namespace Hatch.Cli;

/// <summary>A question the way it is read at a terminal.</summary>
public static class Questions
{
    /// <summary>
    /// Where it came from, who asked, the body, and the answers it offers -
    /// numbered, because a number is what <c>hatch.sh answer</c> takes.
    /// </summary>
    public static IEnumerable<string> Draw(IEnumerable<QuestionDto> questions)
    {
        foreach (var q in questions)
        {
            yield return $"{q.IssueKey}  #{q.Id}  {q.IssueTitle}";
            yield return $"  asked by {q.AskedBy}, {Format.Stamp(q.AskedAt)}";
            yield return "";

            foreach (var line in q.Body.ReplaceLineEndings("\n").Split('\n')) yield return $"  {line}";

            yield return "";

            var options = q.Options ?? [];
            for (var i = 0; i < options.Count; i++)
            {
                yield return $"  [{i + 1}] {options[i].Label}{(options[i].Recommended ? "  (recommended)" : "")}";

                if (options[i].Detail is not { Length: > 0 } detail) continue;
                foreach (var line in detail.ReplaceLineEndings("\n").Split('\n')) yield return $"      {line}";
            }

            yield return "";
        }
    }
}
