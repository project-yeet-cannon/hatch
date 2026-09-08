namespace Aerie.Hatch;

/// <summary>
/// Making the word <c>hatch</c> resolve inside a session this program spawned.
/// </summary>
/// <remarks>
/// <para>The prompt an agent receives names <c>hatch show</c>, <c>hatch
/// comment</c>, <c>hatch ask</c> and the rest (AERIE-934). That instruction is
/// only true if the word resolves, and telling an agent to run a command that
/// is not there costs an increment to find out.</para>
///
/// <para>This process <em>is</em> <c>hatch</c>. So where it is running as a
/// built binary of that name, its own directory goes on the front of the
/// session's <c>PATH</c> - which makes the loop work in a fresh checkout
/// without anybody having installed a copy first, and is the same program an
/// installed copy would be. Where it is running some other way - <c>dotnet
/// run</c>, where the executable is <c>dotnet</c> - there is nothing to point
/// at and nothing is changed, because inventing a path to a binary that may not
/// exist would be worse than leaving <c>PATH</c> alone.</para>
/// </remarks>
public static class Reach
{
    /// <summary>
    /// The directory holding this program, if this program is a binary called
    /// <c>hatch</c>. Null for every other way of being started.
    /// </summary>
    /// <param name="processPath">
    /// <see cref="Environment.ProcessPath"/> in a real run. A parameter so a
    /// test can ask about a path this machine does not have.
    /// </param>
    public static string? OwnDirectory(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return null;

        // The published name is `hatch`, and `hatch.exe` on Windows. Anything
        // else is `dotnet`, a test host, or a rename nobody here can vouch for.
        var name = Path.GetFileNameWithoutExtension(processPath);
        if (!string.Equals(name, "hatch", StringComparison.OrdinalIgnoreCase)) return null;

        return Path.GetDirectoryName(processPath) is { Length: > 0 } directory ? directory : null;
    }

    /// <summary>
    /// <paramref name="directory"/> on the front of <c>PATH</c>, leaving what
    /// was already there behind it.
    /// </summary>
    /// <remarks>
    /// The front rather than the back, so the <c>hatch</c> that answers is the
    /// one that composed the prompt - a session told to comment on a ticket by
    /// one build and doing it through another is a difference nobody would
    /// think to look for. A directory already at the front is left alone rather
    /// than added twice.
    /// </remarks>
    public static void OnPath(IDictionary<string, string?> environment, string? directory)
    {
        if (directory is not { Length: > 0 }) return;

        var existing = environment.TryGetValue("PATH", out var path) ? path ?? "" : "";
        var separator = Path.PathSeparator.ToString();

        if (existing.Length == 0)
        {
            environment["PATH"] = directory;
            return;
        }

        var already = existing.Split(Path.PathSeparator).FirstOrDefault();
        if (string.Equals(already, directory, StringComparison.Ordinal)) return;

        environment["PATH"] = directory + separator + existing;
    }
}
