namespace Aerie.Hatch;

/// <summary>
/// Where Hatch is and how this machine reaches it, asked for once and written
/// where it follows the person rather than the checkout.
/// </summary>
/// <remarks>
/// <para>The one command that has to work before anything is configured, which
/// is why it is the one command <see cref="Settings.TryLoad"/> is not applied
/// to. It reads whatever is already there as its defaults, so changing one
/// setting is not an excuse to retype the others.</para>
///
/// <para>It writes <see cref="Settings.UserConfigPath"/> and nothing else. A
/// checkout's <c>scripts/.env</c> is still read, at higher precedence, for the
/// repository that wants to pin its own origin - but it is no longer written
/// from here, because a CLI installed once and run everywhere cannot keep its
/// settings inside one clone.</para>
/// </remarks>
/// <param name="Environment">
/// What is exported, so the write can leave the process holding the values it
/// just took - a <c>config</c> that could not then reach the board would be a
/// command that verifies nothing.
/// </param>
public sealed record ConfigCommand(
    Terminal Say,
    Input In,
    IDictionary<string, string?> Environment,
    string? CheckoutEnvFile,
    string RunnerName)
{
    public static readonly string[] ConfigUsage =
    [
        "usage: hatch config [--show]",
        "",
        "  hatch config         asks for the origin and the key, and writes them",
        "  hatch config --show  says what is set, and which layer it came from",
        "",
        "  Written to the per-user file, mode 600 where the platform has modes.",
        "  Read back highest-first: an exported variable, then scripts/.env in the",
        "  checkout you are standing in, then that file.",
        "",
        "  The key may be left empty for a Hatch running with its wall off, where",
        "  calls name themselves with a runner header instead. Never in a",
        "  repository, either way.",
    ];

    /// <summary>The per-user file. Named so a test can put one somewhere that is not the machine's own.</summary>
    public string ConfigPath { get; init; } = Settings.UserConfigPath();

    /// <summary>
    /// How the written settings are proved. Replaced by a test, which has no
    /// origin to reach.
    /// </summary>
    public Func<Settings, string, CancellationToken, Task<string?>> Probe { get; init; } =
        async (settings, runnerName, ct) =>
        {
            using var client = new HatchClient(settings, runnerName);
            var board = await new Board(client).BoardAsync(ct);
            return board is null ? null : Columns.Named(board.Statuses);
        };

    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (Usage.Wanted(args)) return Usage.Print(Say, ConfigUsage);
        if (args is ["--show"]) return Show();
        if (args.Length > 0) return Usage.Refuse(Say, "config takes nothing, or --show", ConfigUsage);

        if (!In.Interactive)
        {
            Say.Complain("hatch: config asks questions and needs a terminal");
            return 1;
        }

        var fold = Settings.Layers(CheckoutEnvFile, Environment, ConfigPath);

        // Whatever is loaded already is the default, so changing one setting is
        // not an excuse to retype the other.
        var origin = fold("AERIE_BASE").Value ?? "";
        var key = fold("AERIE_HATCH_KEY").Value ?? "";
        var claudeBin = fold("HATCH_CLAUDE_BIN").Value ?? "";

        Say.Line($"Writing {ConfigPath}. Enter keeps what is shown in brackets.");
        Say.Line("");

        In.Prompt($"Hatch origin [{(origin.Length > 0 ? origin : "https://hatch.<your domain>")}]: ");
        if (In.Line() is { Length: > 0 } typedOrigin) origin = typedOrigin;

        if (origin.Length == 0)
        {
            Say.Complain("hatch: an origin is required");
            return 1;
        }

        if (!origin.StartsWith("http://", StringComparison.Ordinal) &&
            !origin.StartsWith("https://", StringComparison.Ordinal))
        {
            Say.Complain($"hatch: \"{origin}\" has no scheme - every call will fail. Write it as https://...");
            return 1;
        }

        // Read without echo: this is the one value on the screen that a
        // screenshot, a shoulder or a scrollback should not be able to keep.
        In.Prompt($"API key [{(key.Length > 0 ? Mask(key) : "aerie_ak_...")}]: ");
        if (In.Secret() is { Length: > 0 } typedKey) key = typedKey;

        if (key.Length == 0)
            Say.Complain(
                $"hatch: no key - calls will name themselves \"{RunnerName}\", " +
                "which only a Hatch with its wall off reads.");
        else if (!key.StartsWith("aerie_ak_", StringComparison.Ordinal))
            Say.Complain("hatch: warning - that does not start with aerie_ak_. Carrying on; the call below will say.");

        In.Prompt($"claude CLI path, for `work` [{(claudeBin.Length > 0 ? claudeBin : "on PATH")}]: ");
        if (In.Line() is { Length: > 0 } typedBin) claudeBin = typedBin;

        Write(origin, key, claudeBin);

        // Exported, not just set: this process is about to make the call below,
        // and a `work` spawned from here should not have to find the file again.
        Environment["AERIE_BASE"] = origin;
        Environment["AERIE_HATCH_KEY"] = key;
        if (claudeBin.Length > 0) Environment["HATCH_CLAUDE_BIN"] = claudeBin;

        Say.Line("");
        Say.Line($"wrote {ConfigPath}");

        // Written before it is proven, on purpose: a key that is refused is
        // worth keeping on disk to fix, and the message below says what to fix.
        var settings = new Settings { Base = origin.TrimEnd('/'), Key = key };
        try
        {
            var columns = await Probe(settings, RunnerName, ct);
            Say.Line($"reached {settings.Base} - columns: {columns}");
            return 0;
        }
        catch (HatchException e)
        {
            Say.Complain(e.Message);
            Say.Complain("hatch: the file is written but that call did not go through - fix it and run config again.");
            return 1;
        }
    }

    /// <summary>What is set, and which of the three layers it came from.</summary>
    private int Show()
    {
        var fold = Settings.Layers(CheckoutEnvFile, Environment, ConfigPath);
        var exists = File.Exists(ConfigPath) ? "" : " (does not exist yet)";

        Say.Line($"file:             {ConfigPath}{exists}");
        Say.Line($"checkout:         {CheckoutEnvFile ?? "<not in one>"}");

        foreach (var name in Settings.FileNames)
        {
            var (value, from) = fold(name);

            var shown = value is not { Length: > 0 }
                ? name == "AERIE_HATCH_KEY"
                    ? $"<unset - calls go out as \"{RunnerName}\", which only a wall-off Hatch reads>"
                    : "<unset>"
                : name == "AERIE_HATCH_KEY"
                    ? Mask(value)
                    : value;

            var where = from == Settings.Layer.Unset ? "" : $"  ({Where(from)})";
            Say.Line($"{(name + ":").PadRight(18)}{shown}{where}");
        }

        return 0;
    }

    /// <summary>Which layer, as a person would say it.</summary>
    private string Where(Settings.Layer layer) => layer switch
    {
        Settings.Layer.Environment => "exported",
        Settings.Layer.Checkout => CheckoutEnvFile ?? "this checkout",
        Settings.Layer.User => ConfigPath,
        _ => "unset",
    };

    /// <summary>
    /// The file, written whole or not at all.
    /// </summary>
    /// <remarks>
    /// Temp file, then mode, then rename - the rename is what makes a
    /// half-written file impossible to read as a whole one, and the mode is set
    /// before there is anything in it to read. <c>File.SetUnixFileMode</c> is a
    /// no-op concept on Windows, where the file inherits the directory's ACL
    /// and <c>%APPDATA%</c> is already per-user.
    /// </remarks>
    private void Write(string origin, string key, string claudeBin)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

        var temp = $"{ConfigPath}.{System.Environment.ProcessId}";

        var lines = new List<string>
        {
            "# Hatch's settings, written by `hatch config`.",
            "#",
            "# Mode 600, and outside every repository. The key does not belong in a",
            "# commit, a plan, an issue or a paste - Aerie ships to other operators, and",
            "# the aerie_ak_ prefix exists so that one which slips into a diff is",
            "# recognisable on sight. Re-run `config` to change any of this.",
            "",
            $"AERIE_BASE={origin}",

            // Written even when empty, so the file records that the omission was
            // deliberate rather than looking like a half-finished config.
            $"AERIE_HATCH_KEY={key}",
        };

        if (claudeBin.Length > 0) lines.Add($"HATCH_CLAUDE_BIN={claudeBin}");

        File.WriteAllLines(temp, lines);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(temp, ConfigPath, overwrite: true);
    }

    /// <summary>Enough of a key to recognise which one it is, and not enough to use.</summary>
    public static string Mask(string key) => key.Length <= 16 ? "(set)" : $"{key[..12]}...{key[^4..]}";
}
