namespace Aerie.Hatch.Tests;

/// <summary>
/// <c>config</c> - the one command that has to work before anything is
/// configured, and the only one that writes a settings file.
/// </summary>
public sealed class ConfigCommandTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("hatch-config-").FullName;
    private readonly Transcript _say = new();

    private string ConfigPath => Path.Combine(_temp, "hatch", "config");

    private ConfigCommand Command(
        Replies input,
        IDictionary<string, string?>? environment = null,
        string? checkoutEnv = null,
        Func<Settings, string, CancellationToken, Task<string?>>? probe = null) =>
        new(_say, input, environment ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            checkoutEnv, "test:/checkout")
        {
            ConfigPath = ConfigPath,
            Probe = probe ?? ((_, _, _) => Task.FromResult<string?>("To Do, In Progress, Done")),
        };

    private string Said => string.Join("\n", _say.Said);

    private string Complained => string.Join("\n", _say.Complained);

    private string WriteCheckoutEnv(params string[] lines)
    {
        var path = Path.Combine(_temp, "scripts.env");
        File.WriteAllLines(path, lines);
        return path;
    }

    // ---- writing ----

    [Fact]
    public async Task It_asks_for_the_origin_the_key_and_the_cli_and_writes_all_three()
    {
        var input = new Replies("https://hatch.example", "aerie_ak_written", "/opt/claude");

        Assert.Equal(0, await Command(input).RunAsync([], default));

        var written = File.ReadAllLines(ConfigPath);
        Assert.Contains("AERIE_BASE=https://hatch.example", written);
        Assert.Contains("AERIE_HATCH_KEY=aerie_ak_written", written);
        Assert.Contains("HATCH_CLAUDE_BIN=/opt/claude", written);

        Assert.Contains("Hatch origin", input.Asked[0]);
        Assert.Contains("API key", input.Asked[1]);
        Assert.Contains("claude CLI path", input.Asked[2]);
    }

    /// <summary>
    /// The point of the story: it writes where the person is, not where the
    /// checkout is - and it no longer writes a repository's scripts/.env at all.
    /// </summary>
    [Fact]
    public async Task It_writes_the_per_user_file_and_never_the_checkouts_own()
    {
        var checkoutEnv = WriteCheckoutEnv("AERIE_BASE=https://this-repository");

        await Command(new Replies("https://elsewhere", "", ""), checkoutEnv: checkoutEnv).RunAsync([], default);

        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(["AERIE_BASE=https://this-repository"], File.ReadAllLines(checkoutEnv));
    }

    /// <summary>
    /// Whatever is loaded already is the default, so changing one setting is not
    /// an excuse to retype the others.
    /// </summary>
    [Fact]
    public async Task Enter_alone_keeps_what_is_already_there()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllLines(ConfigPath, ["AERIE_BASE=https://kept", "AERIE_HATCH_KEY=aerie_ak_alreadykept"]);

        var input = new Replies("", "", "");
        Assert.Equal(0, await Command(input).RunAsync([], default));

        var written = File.ReadAllLines(ConfigPath);
        Assert.Contains("AERIE_BASE=https://kept", written);
        Assert.Contains("AERIE_HATCH_KEY=aerie_ak_alreadykept", written);

        // Shown back masked, and never whole.
        Assert.Contains("aerie_ak_alr...kept", input.Asked[1]);
        Assert.DoesNotContain("aerie_ak_alreadykept", input.Asked[1]);
    }

    /// <summary>
    /// Written even when empty, so the file records that the omission was
    /// deliberate rather than looking like a half-finished config.
    /// </summary>
    [Fact]
    public async Task An_empty_key_is_written_and_said_out_loud()
    {
        Assert.Equal(0, await Command(new Replies("https://hatch.example", "", "")).RunAsync([], default));

        Assert.Contains("AERIE_HATCH_KEY=", File.ReadAllLines(ConfigPath));
        Assert.Contains("no key - calls will name themselves \"test:/checkout\"", Complained);
        Assert.Contains("only a Hatch with its wall off reads", Complained);
    }

    [Fact]
    public async Task A_key_with_the_wrong_prefix_is_a_warning_and_not_a_refusal()
    {
        Assert.Equal(0, await Command(new Replies("https://hatch.example", "hunter2", "")).RunAsync([], default));

        Assert.Contains("does not start with aerie_ak_", Complained);
        Assert.Contains("AERIE_HATCH_KEY=hunter2", File.ReadAllLines(ConfigPath));
    }

    [Fact]
    public async Task An_origin_with_no_scheme_is_refused_and_nothing_is_written()
    {
        Assert.Equal(1, await Command(new Replies("hatch.example", "", "")).RunAsync([], default));

        Assert.Contains("has no scheme - every call will fail", Complained);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task No_origin_at_all_is_refused_and_nothing_is_written()
    {
        Assert.Equal(1, await Command(new Replies("", "", "")).RunAsync([], default));

        Assert.Contains("an origin is required", Complained);
        Assert.False(File.Exists(ConfigPath));
    }

    /// <summary>
    /// Mode 600, because the point of the file is that a credential need not be
    /// exported into every shell the day starts with.
    /// </summary>
    [Fact]
    public async Task The_file_is_readable_by_its_owner_and_by_nobody_else()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows has no unix file mode; %APPDATA% is already per-user.");

        await Command(new Replies("https://hatch.example", "aerie_ak_secret", "")).RunAsync([], default);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ConfigPath));
    }

    /// <summary>
    /// Temp file then rename, so a half-written file is never read as a whole
    /// one - and so a second run leaves exactly one file behind.
    /// </summary>
    [Fact]
    public async Task Writing_it_twice_leaves_one_file_and_no_temporary_beside_it()
    {
        await Command(new Replies("https://first", "", "")).RunAsync([], default);
        await Command(new Replies("https://second", "", "")).RunAsync([], default);

        Assert.Equal(["config"], Directory.GetFiles(Path.GetDirectoryName(ConfigPath)!)
            .Select(Path.GetFileName).Order());
        Assert.Contains("AERIE_BASE=https://second", File.ReadAllLines(ConfigPath));
    }

    /// <summary>
    /// Written before it is proven, on purpose: a key that is refused is worth
    /// keeping on disk to fix, and the message says what to fix.
    /// </summary>
    [Fact]
    public async Task A_call_that_does_not_go_through_still_leaves_the_file_behind()
    {
        var code = await Command(
                new Replies("https://hatch.example", "aerie_ak_wrong", ""),
                probe: (_, _, _) => throw new HatchException("hatch: 401 - the key was not accepted."))
            .RunAsync([], default);

        Assert.Equal(1, code);
        Assert.True(File.Exists(ConfigPath));
        Assert.Contains("the file is written but that call did not go through", Complained);
    }

    [Fact]
    public async Task A_call_that_goes_through_names_the_columns_it_found()
    {
        Assert.Equal(0, await Command(new Replies("https://hatch.example", "aerie_ak_ok", "")).RunAsync([], default));

        Assert.Contains("reached https://hatch.example - columns: To Do, In Progress, Done", Said);
    }

    [Fact]
    public async Task Config_without_a_terminal_is_refused_before_it_asks_anything()
    {
        var input = new Replies { AtATerminal = false };

        Assert.Equal(1, await Command(input).RunAsync([], default));
        Assert.Contains("config asks questions and needs a terminal", Complained);
        Assert.Empty(input.Asked);
        Assert.False(File.Exists(ConfigPath));
    }

    // ---- --origin ----

    /// <summary>
    /// The point of the mode: what Hatch's own Runner page prints, pasted on a
    /// machine that has just downloaded the binary.
    /// </summary>
    [Fact]
    public async Task Origin_writes_the_origin_without_asking_anything()
    {
        var input = new Replies();

        Assert.Equal(0, await Command(input).RunAsync(["--origin", "https://hatch.example"], default));

        Assert.Contains("AERIE_BASE=https://hatch.example", File.ReadAllLines(ConfigPath));
        Assert.Empty(input.Asked);
        Assert.Contains("reached https://hatch.example - columns: To Do, In Progress, Done", Said);
    }

    /// <summary>
    /// The sharp one. A command that silently blanked a credential would be a
    /// worse way to lose one than forgetting it.
    /// </summary>
    [Fact]
    public async Task Origin_leaves_the_key_and_the_cli_exactly_as_they_were()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllLines(ConfigPath, [
            "AERIE_BASE=https://old",
            "AERIE_HATCH_KEY=aerie_ak_alreadykept",
            "HATCH_CLAUDE_BIN=/opt/claude",
        ]);

        Assert.Equal(0, await Command(new Replies()).RunAsync(["--origin", "https://new"], default));

        var written = File.ReadAllLines(ConfigPath);
        Assert.Contains("AERIE_BASE=https://new", written);
        Assert.Contains("AERIE_HATCH_KEY=aerie_ak_alreadykept", written);
        Assert.Contains("HATCH_CLAUDE_BIN=/opt/claude", written);
    }

    /// <summary>Never on the screen, even in the mode that never asks for it.</summary>
    [Fact]
    public async Task Origin_never_prints_the_key_it_carried_forward()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllLines(ConfigPath, ["AERIE_HATCH_KEY=aerie_ak_abcdefghijklmnop"]);

        await Command(new Replies()).RunAsync(["--origin", "https://hatch.example"], default);

        Assert.DoesNotContain("aerie_ak_abcdefghijklmnop", $"{Said}\n{Complained}");
    }

    /// <summary>
    /// The same sentence the interactive path says, because it is the same
    /// helper - a friend who paired the page's command with a wall-off Hatch
    /// should be told what their calls will name themselves.
    /// </summary>
    [Fact]
    public async Task Origin_with_no_key_anywhere_says_what_calls_will_name_themselves()
    {
        Assert.Equal(0, await Command(new Replies()).RunAsync(["--origin", "https://hatch.example"], default));

        Assert.Contains("AERIE_HATCH_KEY=", File.ReadAllLines(ConfigPath));
        Assert.Contains("no key - calls will name themselves \"test:/checkout\"", Complained);
    }

    [Fact]
    public async Task Origin_with_no_scheme_is_refused_with_the_same_sentence_and_nothing_is_written()
    {
        Assert.Equal(1, await Command(new Replies()).RunAsync(["--origin", "hatch.example"], default));

        Assert.Contains("has no scheme - every call will fail", Complained);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task An_empty_origin_is_refused_and_nothing_is_written()
    {
        Assert.Equal(1, await Command(new Replies()).RunAsync(["--origin", "   "], default));

        Assert.Contains("an origin is required", Complained);
        Assert.False(File.Exists(ConfigPath));
    }

    /// <summary>Written before it is proven, exactly as the interactive path writes it.</summary>
    [Fact]
    public async Task Origin_that_cannot_be_reached_still_leaves_the_file_behind()
    {
        var code = await Command(
                new Replies(),
                probe: (_, _, _) => throw new HatchException("hatch: 401 - the key was not accepted."))
            .RunAsync(["--origin", "https://hatch.example"], default);

        Assert.Equal(1, code);
        Assert.Contains("AERIE_BASE=https://hatch.example", File.ReadAllLines(ConfigPath));
        Assert.Contains("the file is written but that call did not go through", Complained);
    }

    /// <summary>
    /// It needs no terminal - which is the difference from `config` and the
    /// reason a setup script may use it.
    /// </summary>
    [Fact]
    public async Task Origin_works_where_there_is_no_terminal_at_all()
    {
        var input = new Replies { AtATerminal = false };

        Assert.Equal(0, await Command(input).RunAsync(["--origin", "https://hatch.example"], default));
        Assert.Contains("AERIE_BASE=https://hatch.example", File.ReadAllLines(ConfigPath));
    }

    // ---- --show ----

    [Fact]
    public async Task Show_says_which_of_the_three_layers_each_value_came_from()
    {
        var checkoutEnv = WriteCheckoutEnv("AERIE_BASE=https://this-repository");
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllLines(ConfigPath, ["AERIE_HATCH_KEY=aerie_ak_from_the_user_file"]);

        var code = await Command(
                new Replies(),
                environment: new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["HATCH_CLAUDE_BIN"] = "/opt/claude",
                },
                checkoutEnv: checkoutEnv)
            .RunAsync(["--show"], default);

        Assert.Equal(0, code);
        Assert.Contains($"AERIE_BASE:        https://this-repository  ({checkoutEnv})", Said);
        Assert.Contains($"AERIE_HATCH_KEY:   aerie_ak_fro...file  ({ConfigPath})", Said);
        Assert.Contains("HATCH_CLAUDE_BIN:  /opt/claude  (exported)", Said);
        Assert.Contains("HATCH_BASE_BRANCH: <unset>", Said);
    }

    /// <summary>Enough of a key to recognise which one it is, and not enough to use.</summary>
    [Fact]
    public async Task Show_masks_the_key_and_never_prints_it_whole()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllLines(ConfigPath, ["AERIE_HATCH_KEY=aerie_ak_abcdefghijklmnop"]);

        await Command(new Replies()).RunAsync(["--show"], default);

        Assert.DoesNotContain("aerie_ak_abcdefghijklmnop", Said);
        Assert.Contains("aerie_ak_abc...mnop", Said);
    }

    [Fact]
    public void A_key_too_short_to_mask_usefully_is_only_reported_as_set()
    {
        Assert.Equal("(set)", ConfigCommand.Mask("short"));
        Assert.Equal("aerie_ak_abc...mnop", ConfigCommand.Mask("aerie_ak_abcdefghijklmnop"));
    }

    /// <summary>
    /// With no key, what a call will name itself is the thing worth printing -
    /// only a wall-off Hatch reads it, and knowing that is half of diagnosing a
    /// 401.
    /// </summary>
    [Fact]
    public async Task Show_with_no_key_says_what_calls_will_name_themselves_instead()
    {
        await Command(new Replies()).RunAsync(["--show"], default);

        Assert.Contains("<unset - calls go out as \"test:/checkout\"", Said);
        Assert.Contains("(does not exist yet)", Said);
    }

    [Fact]
    public async Task Show_says_it_is_not_in_a_checkout_when_it_is_not()
    {
        await Command(new Replies()).RunAsync(["--show"], default);
        Assert.Contains("checkout:         <not in one>", Said);
    }

    // ---- usage ----

    [Fact]
    public async Task Anything_other_than_the_three_modes_is_the_mistake_and_then_the_usage_block()
    {
        Assert.Equal(1, await Command(new Replies()).RunAsync(["--everything"], default));
        Assert.Contains("config takes nothing, --show, or --origin <origin>", Complained);
        Assert.Contains("usage: hatch config", Complained);
    }

    /// <summary>An origin is one argument, and a bare `--origin` is a mistake rather than a prompt.</summary>
    [Fact]
    public async Task Origin_with_nothing_after_it_is_the_usage_block_and_not_a_question()
    {
        var input = new Replies();

        Assert.Equal(1, await Command(input).RunAsync(["--origin"], default));
        Assert.Contains("config takes nothing, --show, or --origin <origin>", Complained);
        Assert.Empty(input.Asked);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task Dash_h_prints_its_own_usage_and_writes_nothing()
    {
        Assert.Equal(0, await Command(new Replies()).RunAsync(["-h"], default));
        Assert.StartsWith("usage: hatch config", Said);
        Assert.False(File.Exists(ConfigPath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that will not go is not a failing test.
        }
    }
}
