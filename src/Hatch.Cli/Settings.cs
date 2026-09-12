namespace Hatch.Cli;

/// <summary>
/// Where to reach Hatch, and the handful of things about this machine that
/// cannot be written down in the repository.
/// </summary>
/// <remarks>
/// <para>Three sources, highest first: anything already exported, then
/// <c>scripts/.env</c> in the checkout the command was run from, then the
/// per-user file at <see cref="UserConfigPath"/>. That order is what makes a
/// one-off origin a prefix on a command line rather than an edit to a file -
/// and what lets one person's settings follow them into every checkout while a
/// single repository can still pin its own.</para>
///
/// <para>Each layer is parsed against an allowlist rather than sourced, since a
/// credential file that is also a program is a larger promise than "a few
/// values".</para>
///
/// <para>The key is optional. A Hatch started with its wall off has no
/// credential to present and names its callers by the runner header instead
/// (docs/auth-architecture.md, "Local mode"), so a missing key is a mode and
/// not a failure to load; <see cref="HatchClient"/> is where the two diverge.</para>
/// </remarks>
public sealed record Settings
{
    /// <summary>Everything a settings file may carry. A line naming anything else is skipped, loudly.</summary>
    public static readonly string[] FileNames =
    [
        "HATCH_BASE", "HATCH_KEY", "HATCH_CLAUDE_BIN", "HATCH_BASE_BRANCH", "HATCH_RUNNER",
    ];

    /// <summary>Which of the three layers a value came from, for <c>config --show</c>.</summary>
    public enum Layer
    {
        /// <summary>Nowhere. Nothing set it.</summary>
        Unset,

        /// <summary>Exported into this process. Wins over both files.</summary>
        Environment,

        /// <summary>This checkout's <c>scripts/.env</c>.</summary>
        Checkout,

        /// <summary>The per-user file, which follows the person between checkouts.</summary>
        User,
    }

    /// <summary>The origin, with no trailing slash.</summary>
    public required string Base { get; init; }

    /// <summary>
    /// The <c>hatch_ak_…</c> key, or empty against a Hatch with its wall off.
    /// Never printed, never logged, never written down here.
    /// </summary>
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

    /// <summary>Where each value came from, so <c>config --show</c> can say.</summary>
    public IReadOnlyDictionary<string, Layer> Sources { get; init; } =
        new Dictionary<string, Layer>(StringComparer.Ordinal);

    /// <summary>
    /// The settings file that belongs to the person rather than to a checkout:
    /// <c>ApplicationData/hatch/config</c>, which is <c>~/.config/hatch/config</c>
    /// on Unix and <c>%APPDATA%\hatch\config</c> on Windows.
    /// </summary>
    /// <remarks>
    /// The whole reason this story exists: an installed <c>hatch</c> is run from
    /// wherever somebody happens to be standing, and settings that lived in one
    /// repository's <c>scripts/</c> directory were settings that only worked in
    /// one repository.
    /// </remarks>
    public static string UserConfigPath() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "hatch",
        "config");

    /// <summary>
    /// The three layers, folded. Answers the settings, or the sentence saying
    /// what is missing - which is the origin alone, since the key is optional.
    /// </summary>
    /// <param name="checkoutEnvFile">
    /// This checkout's <c>scripts/.env</c>, or null where the command was not
    /// run inside one. Fourteen of the sixteen commands do not need a checkout
    /// and pass null here.
    /// </param>
    /// <param name="userConfigFile">
    /// The per-user file. Named rather than looked up so a test can put one
    /// somewhere that is not the machine's own.
    /// </param>
    public static bool TryLoad(
        string? checkoutEnvFile,
        IDictionary<string, string?> environment,
        out Settings settings,
        out string refusal,
        string? userConfigFile = null)
    {
        var sources = new Dictionary<string, Layer>(StringComparer.Ordinal);
        var fold = Layers(checkoutEnvFile, environment, userConfigFile);

        string? Read(string name)
        {
            var (value, from) = fold(name);
            sources[name] = from;
            return value;
        }

        settings = null!;
        refusal = "";

        var origin = Read("HATCH_BASE");
        var key = Read("HATCH_KEY");
        var claudeBin = Read("HATCH_CLAUDE_BIN");
        var baseBranch = Read("HATCH_BASE_BRANCH");
        var runner = Read("HATCH_RUNNER");
        var heartbeat = Read("HATCH_HEARTBEAT");

        // The origin alone. A key is not required, because a Hatch with its wall
        // off has no credential to present - and a load that refused without one
        // would make local mode unreachable from the command that configures it.
        if (string.IsNullOrWhiteSpace(origin))
        {
            refusal = string.Join('\n',
                "hatch: not configured - it needs an origin.",
                "",
                "    hatch config          asks for it, and writes it where it follows you",
                "    hatch config --show   says what is set, and which layer it came from",
                "",
                "  Or export HATCH_BASE. The key may be left empty for a Hatch running with",
                "  its wall off. Never in a repository, either way: Hatch ships to other",
                "  operators, and a key in the artifact is one operator's key inherited by",
                "  everybody who clones it.");
            return false;
        }

        var pulse = 20;
        if (heartbeat is { } beat && int.TryParse(beat, out var parsed) && parsed >= 0) pulse = parsed;

        settings = new Settings
        {
            Base = origin.TrimEnd('/'),
            Key = key?.Trim() ?? "",
            ClaudeBin = claudeBin,
            BaseBranch = baseBranch,
            Runner = runner,
            HeartbeatSeconds = pulse,
            Sources = sources,
        };
        return true;
    }

    /// <summary>Where a value came from, or <see cref="Layer.Unset"/> if nothing set it.</summary>
    public Layer SourceOf(string name) => Sources.GetValueOrDefault(name, Layer.Unset);

    /// <summary>
    /// The three layers as one lookup: the value, and which layer it came from.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryLoad"/> because <c>config --show</c> has to
    /// answer on a machine that has nothing configured yet, and a load that
    /// refuses for want of an origin cannot say where the origin is missing
    /// from.
    /// </remarks>
    public static Func<string, (string? Value, Layer From)> Layers(
        string? checkoutEnvFile, IDictionary<string, string?> environment, string? userConfigFile = null)
    {
        var checkout = ReadFile(checkoutEnvFile);
        var user = ReadFile(userConfigFile ?? UserConfigPath());

        return name =>
        {
            if (environment.TryGetValue(name, out var exported) && !string.IsNullOrWhiteSpace(exported))
                return (exported, Layer.Environment);

            if (checkout.TryGetValue(name, out var here) && !string.IsNullOrWhiteSpace(here))
                return (here, Layer.Checkout);

            if (user.TryGetValue(name, out var mine) && !string.IsNullOrWhiteSpace(mine))
                return (mine, Layer.User);

            return (null, Layer.Unset);
        };
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
