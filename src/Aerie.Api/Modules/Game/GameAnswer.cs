using System.Text.RegularExpressions;

namespace Aerie.Api.Modules.Game;

/// <summary>The three blocks a turn comes back as - see GameEngineReference's "How to answer".</summary>
public record GameAnswer(string Code, string Summary, string? Extra);

/// <summary>
/// Reads a model's reply into <see cref="GameAnswer"/>.
/// </summary>
/// <remarks>
/// Its own type rather than a couple of private helpers on GameAuthor, because
/// this is the part that has to survive contact with a real answer and the rest
/// of GameAuthor cannot be exercised without an API key. Everything here is
/// tolerant on purpose: a missing closing tag, a markdown fence the model
/// wrapped around code that was already inside tags, stray prose before the
/// first tag. None of those are worth losing a turn a child is waiting on, and
/// all of them happen.
/// </remarks>
public static partial class GameAnswerParser
{
    /// <summary>The answer, or null when there is no code in it - which is the one thing that cannot be worked around.</summary>
    public static GameAnswer? Parse(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;

        var code = ExtractCode(answer);
        if (code is null) return null;

        return new GameAnswer(code, Tagged(answer, "summary") ?? "Something new!", Tagged(answer, "extra"));
    }

    private static string? ExtractCode(string answer)
    {
        var raw = Tagged(answer, "code");
        if (raw is null) return null;

        // A stray ```js line is a syntax error in a file that is otherwise
        // perfect, so a fence inside the tags is peeled rather than kept.
        var lines = raw.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal)) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].TrimEnd().EndsWith("```", StringComparison.Ordinal)) lines.RemoveAt(lines.Count - 1);

        var code = string.Join('\n', lines).Trim();
        return code.Length == 0 ? null : code;
    }

    /// <summary>
    /// The contents of one tag, accepting a missing closing tag on the last
    /// block - which is exactly what an answer cut short by the token ceiling
    /// looks like. Whether such an answer is usable is decided upstream by its
    /// stop reason, not here.
    /// </summary>
    private static string? Tagged(string answer, string tag)
    {
        var match = Regex.Match(
            answer,
            $"<{tag}>(?<body>.*?)(</{tag}>|$)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        if (!match.Success) return null;
        var body = match.Groups["body"].Value.Trim();
        return body.Length == 0 ? null : body;
    }
}
