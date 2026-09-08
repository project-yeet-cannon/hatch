namespace Aerie.Hatch;

/// <summary>
/// The three things every subcommand does with its own usage block.
/// </summary>
/// <remarks>
/// Named once because sixteen commands each recognising <c>-h</c> is sixteen
/// chances to spell one of them differently, and because a refusal that does not
/// then say what the command does take is a round trip for nothing.
/// </remarks>
public static class Usage
{
    /// <summary>
    /// Whether the arguments are asking what this command takes.
    /// </summary>
    /// <remarks>
    /// The first argument only, and deliberately. Four of these commands take
    /// prose - a comment body, a question, a typed answer - and a body that
    /// happens to read <c>--help</c> is a body somebody meant to post, not a
    /// request for the usage block.
    /// </remarks>
    public static bool Wanted(string[] args) => args is [("-h" or "--help"), ..];

    /// <summary>The block, on standard output, because it was asked for.</summary>
    public static int Print(Terminal say, IEnumerable<string> usage)
    {
        say.Lines(usage);
        return 0;
    }

    /// <summary>
    /// The mistake and then the block, both on standard error, because neither
    /// was asked for.
    /// </summary>
    public static int Refuse(Terminal say, string what, IEnumerable<string> usage)
    {
        say.Complain($"hatch: {what}");
        say.Complain("");
        foreach (var line in usage) say.Complain(line);
        return 1;
    }
}
