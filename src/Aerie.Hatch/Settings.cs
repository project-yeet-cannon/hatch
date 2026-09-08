namespace Aerie.Hatch;

/// <summary>
/// Where to reach Hatch, and the handful of things about this machine that
/// cannot be written down in the repository.
/// </summary>
/// <remarks>
/// The same two sources <c>scripts/hatch.sh</c> reads, in the same order and
/// with the same allowlist: <c>scripts/.env</c> underneath, and anything
/// already exported over the top of it. That order is what makes a one-off
/// origin a prefix on a command line rather than an edit to a file - and the
/// allowlist is why the file is parsed rather than sourced, since a credential
/// file that is also a program is a larger promise than "a few values".
/// </remarks>
public sealed record Settings
{
    /// <summary>Everything the file may carry. A line naming anything else is skipped, loudly.</summary>
    public static readonly string[] FileNames =
    [
        "AERIE_BASE", "AERIE_HATCH_KEY", "HATCH_CLAUDE_BIN", "HATCH_BASE_BRANCH", "HATCH_RUNNER",
    ];

    /// <summary>The origin, with no trailing slash.</summary>
    public required string Base { get; init; }

    /// <summary>The <c>aerie_ak_…</c> key. Never printed, never logged, never written down here.</summary>
    public required string Key { get; init; }

    /// <summary>The claude CLI, if it is not simply on PATH.</summary>
    public string? ClaudeBin { get; init; }

    /// <summary>The trunk the loop resets to, when it is not the one origin calls its default.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>What the board calls this runner, overriding <c>host:/path/to/checkout</c>.</summary>
    public string? Runner { get; init; }

    /// <summary>
    /// How long a spawned session may say nothing before the renderer says what
    /// it is still waiting on. Zero turns the pulse off.
    /// </summary>
    public int HeartbeatSeconds { get; init; } = 20;

    /// <summary>
    /// Read the environment, layered over <paramref name="envFile"/> if it is
    /// there. Answers the settings or the sentence saying what is missing.
    /// </summary>
    public static bool TryLoad(
        string? envFile, IDictionary<string, string?> environment, out Settings settings, out string refusal)
    {
        var file = ReadFile(envFile);

        string? Read(string name) =>
            environment.TryGetValue(name, out var exported) && !string.IsNullOrWhiteSpace(exported)
                ? exported
                : file.GetValueOrDefault(name);

        settings = null!;
        refusal = "";

        var origin = Read("AERIE_BASE");
        var key = Read("AERIE_HATCH_KEY");

        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(key))
        {
            refusal = string.Join('\n',
                "hatch: needs an origin and a key.",
                "",
                "    ./scripts/hatch.sh config          asks for them, and writes scripts/.env",
                "    ./scripts/hatch.sh config --show   says what is set, and where it came from",
                "",
                "  Or export AERIE_BASE and AERIE_HATCH_KEY. Never in this repository:",
                "  Aerie ships to other operators, and a key in the artifact is one",
                "  operator's key inherited by everybody who clones it.");
            return false;
        }

        var pulse = 20;
        if (Read("HATCH_HEARTBEAT") is { } beat && int.TryParse(beat, out var parsed) && parsed >= 0) pulse = parsed;

        settings = new Settings
        {
            Base = origin.TrimEnd('/'),
            Key = key.Trim(),
            ClaudeBin = Read("HATCH_CLAUDE_BIN"),
            BaseBranch = Read("HATCH_BASE_BRANCH"),
            Runner = Read("HATCH_RUNNER"),
            HeartbeatSeconds = pulse,
        };
        return true;
    }

    /// <summary>
    /// <c>KEY=value</c> a line, blanks and <c>#</c> comments skipped, one layer
    /// of surrounding quotes stripped - and a name that is not on the allowlist
    /// ignored with a sentence saying so, rather than silently.
    /// </summary>
    internal static Dictionary<string, string> ReadFile(string? path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return values;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var split = line.IndexOf('=');
            if (split <= 0) continue;

            var name = line[..split].Trim();
            var value = line[(split + 1)..].Trim();

            if (!FileNames.Contains(name, StringComparer.Ordinal))
            {
                Console.Error.WriteLine(
                    $"hatch: ignoring \"{name}\" in {path} - not one of: {string.Join(' ', FileNames)}");
                continue;
            }

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            values[name] = value;
        }

        return values;
    }
}
