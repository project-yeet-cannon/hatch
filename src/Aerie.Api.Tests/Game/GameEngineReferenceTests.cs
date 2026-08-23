using System.Text.RegularExpressions;
using Aerie.Api.Modules.Game;

namespace Aerie.Api.Tests.Game;

/// <summary>
/// Holds the two halves of the engine contract together: the JavaScript the
/// game runs on (Aerie.Web/apps/family/src/modules/game/runtime/engine.js) and
/// the prose the model is given about it (GameEngineReference.SystemPrompt).
/// </summary>
/// <remarks>
/// This is the only test in the repo that reaches across into the web app's
/// source, and it earns the exception. Every other mismatch in Aerie is a
/// compile error or a failing request; this one is neither. Rename a method in
/// the engine and nothing at all goes red - the next game a child asks for
/// simply calls something that no longer exists, and the first anyone hears
/// about it is a crash on a tablet. A prompt is an interface, and this is the
/// only place it can be type-checked.
/// </remarks>
public class GameEngineReferenceTests
{
    /// <summary>
    /// Documented as prose rather than as a call, so the "must appear as w.X"
    /// rule below would fail on them for no reason.
    /// </summary>
    private static readonly HashSet<string> DocumentedInProse = [];

    [Fact]
    public void EveryEngineMethod_IsInTheSystemPrompt()
    {
        var missing = WorldMembers()
            .Where(member => !DocumentedInProse.Contains(member))
            .Where(member => !GameEngineReference.SystemPrompt.Contains($"w.{member}", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"engine.js exposes {string.Join(", ", missing)} but the system prompt never mentions them - "
            + "a model cannot call what nobody told it about.");
    }

    [Fact]
    public void EveryMethodTheSystemPromptPromises_ExistsInTheEngine()
    {
        var members = WorldMembers();

        var invented = Regex.Matches(GameEngineReference.SystemPrompt, @"\bw\.([A-Za-z][A-Za-z0-9]*)")
            .Select(match => match.Groups[1].Value)
            .Distinct()
            .Where(name => !members.Contains(name))
            .ToList();

        Assert.True(
            invented.Count == 0,
            $"The system prompt promises {string.Join(", ", invented)}, which engine.js does not have - "
            + "every game that takes it at its word crashes.");
    }

    /// <summary>
    /// The members of the `world` object literal in engine.js: methods,
    /// getters, and the two plain properties. Parsed from the source rather
    /// than listed here, because a list here would be a third copy to forget to
    /// update - which is the failure this whole file exists to prevent.
    /// </summary>
    private static HashSet<string> WorldMembers()
    {
        var source = File.ReadAllText(EnginePath());

        var start = source.IndexOf("    const world = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "engine.js no longer declares `const world = {` - this test needs updating with it.");

        var end = source.IndexOf("\n    };", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of the world object in engine.js.");

        var body = source[start..end];
        var members = Regex.Matches(body, @"^      (?:get )?([A-Za-z][A-Za-z0-9]*)\s*[(:]", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(members.Count > 20, $"Only found {members.Count} members on the world object - the parse is wrong, not the engine.");
        return members;
    }

    /// <summary>
    /// Walks up from the test binary to the repo, rather than assuming how deep
    /// bin/Debug/net10.0 happens to be today.
    /// </summary>
    private static string EnginePath()
    {
        const string relative = "src/Aerie.Web/apps/family/src/modules/game/runtime/engine.js";

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not find {relative} above {AppContext.BaseDirectory}.");
    }
}
