namespace Aerie.Hatch.Tests;

/// <summary>
/// The three layers, and the two things about them that would be silent
/// failures: which one wins, and that a missing key is a mode rather than a
/// refusal.
/// </summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("hatch-settings-").FullName;

    private string Write(string name, params string[] lines)
    {
        var path = Path.Combine(_temp, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    private static Dictionary<string, string?> Env(params (string Name, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    // ---- precedence ----

    /// <summary>
    /// What makes a one-off origin a prefix on a command line rather than an
    /// edit to a file.
    /// </summary>
    [Fact]
    public void An_exported_value_beats_both_files()
    {
        var checkout = Write("checkout.env", "AERIE_BASE=https://from-the-checkout");
        var user = Write("user.config", "AERIE_BASE=https://from-the-user-file");

        Assert.True(Settings.TryLoad(
            checkout, Env(("AERIE_BASE", "https://exported")), out var settings, out _, user));

        Assert.Equal("https://exported", settings.Base);
        Assert.Equal(Settings.Layer.Environment, settings.SourceOf("AERIE_BASE"));
    }

    /// <summary>
    /// A repository that pins its own origin keeps it, which is what the
    /// checkout layer is for.
    /// </summary>
    [Fact]
    public void This_checkouts_env_beats_the_per_user_file()
    {
        var checkout = Write("checkout.env", "AERIE_BASE=https://from-the-checkout");
        var user = Write("user.config", "AERIE_BASE=https://from-the-user-file");

        Assert.True(Settings.TryLoad(checkout, Env(), out var settings, out _, user));

        Assert.Equal("https://from-the-checkout", settings.Base);
        Assert.Equal(Settings.Layer.Checkout, settings.SourceOf("AERIE_BASE"));
    }

    /// <summary>
    /// The whole reason the per-user file exists: `hatch board` from a directory
    /// that has never been a repository still knows where Hatch is.
    /// </summary>
    [Fact]
    public void The_per_user_file_answers_when_there_is_no_checkout_at_all()
    {
        var user = Write("user.config", "AERIE_BASE=https://from-the-user-file", "AERIE_HATCH_KEY=aerie_ak_mine");

        Assert.True(Settings.TryLoad(null, Env(), out var settings, out _, user));

        Assert.Equal("https://from-the-user-file", settings.Base);
        Assert.Equal("aerie_ak_mine", settings.Key);
        Assert.Equal(Settings.Layer.User, settings.SourceOf("AERIE_HATCH_KEY"));
    }

    /// <summary>
    /// Layered a value at a time, not a file at a time - so a checkout may pin
    /// the origin while the key still comes from the person.
    /// </summary>
    [Fact]
    public void Each_value_is_layered_on_its_own()
    {
        var checkout = Write("checkout.env", "AERIE_BASE=https://this-repository");
        var user = Write("user.config", "AERIE_BASE=https://mine", "AERIE_HATCH_KEY=aerie_ak_mine");

        Assert.True(Settings.TryLoad(
            checkout, Env(("HATCH_CLAUDE_BIN", "/opt/claude")), out var settings, out _, user));

        Assert.Equal("https://this-repository", settings.Base);
        Assert.Equal("aerie_ak_mine", settings.Key);
        Assert.Equal("/opt/claude", settings.ClaudeBin);

        Assert.Equal(Settings.Layer.Checkout, settings.SourceOf("AERIE_BASE"));
        Assert.Equal(Settings.Layer.User, settings.SourceOf("AERIE_HATCH_KEY"));
        Assert.Equal(Settings.Layer.Environment, settings.SourceOf("HATCH_CLAUDE_BIN"));
    }

    /// <summary>An exported variable that is there but empty is not a value.</summary>
    [Fact]
    public void An_empty_exported_value_falls_through_to_the_file_below_it()
    {
        var user = Write("user.config", "AERIE_BASE=https://mine");

        Assert.True(Settings.TryLoad(null, Env(("AERIE_BASE", "")), out var settings, out _, user));

        Assert.Equal("https://mine", settings.Base);
        Assert.Equal(Settings.Layer.User, settings.SourceOf("AERIE_BASE"));
    }

    [Fact]
    public void Nothing_anywhere_is_unset_and_not_a_guess()
    {
        Assert.True(Settings.TryLoad(
            null, Env(("AERIE_BASE", "https://somewhere")), out var settings, out _,
            Path.Combine(_temp, "does-not-exist")));

        Assert.Equal(Settings.Layer.Unset, settings.SourceOf("HATCH_BASE_BRANCH"));
        Assert.Null(settings.BaseBranch);
    }

    // ---- the key is optional ----

    /// <summary>
    /// A Hatch with its wall off has no credential to present, so a missing key
    /// is a mode and not a failure to load. A load that refused without one
    /// would make local mode unreachable from the command that configures it.
    /// </summary>
    [Fact]
    public void An_origin_with_no_key_loads_and_the_key_is_empty()
    {
        Assert.True(Settings.TryLoad(
            null, Env(("AERIE_BASE", "http://localhost:5227")), out var settings, out var refusal,
            Path.Combine(_temp, "does-not-exist")));

        Assert.Equal("", settings.Key);
        Assert.Equal("", refusal);
    }

    [Fact]
    public void No_origin_anywhere_is_the_one_refusal_and_it_names_config()
    {
        Assert.False(Settings.TryLoad(
            null, Env(("AERIE_HATCH_KEY", "aerie_ak_x")), out _, out var refusal,
            Path.Combine(_temp, "does-not-exist")));

        Assert.Contains("not configured - it needs an origin", refusal);
        Assert.Contains("hatch config", refusal);
        Assert.DoesNotContain("./scripts/hatch.sh", refusal);
    }

    // ---- the file itself ----

    [Fact]
    public void A_trailing_slash_on_the_origin_is_dropped_so_no_path_ever_doubles_up()
    {
        Assert.True(Settings.TryLoad(
            null, Env(("AERIE_BASE", "https://hatch.example/")), out var settings, out _,
            Path.Combine(_temp, "none")));

        Assert.Equal("https://hatch.example", settings.Base);
    }

    [Fact]
    public void One_layer_of_surrounding_quotes_is_stripped()
    {
        var user = Write("user.config", """AERIE_BASE="https://quoted" """.TrimEnd(), "AERIE_HATCH_KEY='aerie_ak_q'");

        Assert.True(Settings.TryLoad(null, Env(), out var settings, out _, user));

        Assert.Equal("https://quoted", settings.Base);
        Assert.Equal("aerie_ak_q", settings.Key);
    }

    [Fact]
    public void Blank_lines_and_comments_are_skipped()
    {
        var user = Write("user.config", "# written by config", "", "AERIE_BASE=https://mine", "   ");

        Assert.True(Settings.TryLoad(null, Env(), out var settings, out _, user));
        Assert.Equal("https://mine", settings.Base);
    }

    /// <summary>
    /// Parsed against an allowlist rather than sourced, because a credential
    /// file that is also a program is a larger promise than "a few values".
    /// </summary>
    [Fact]
    public void A_name_that_is_not_on_the_allowlist_is_ignored()
    {
        var user = Write("user.config", "AERIE_BASE=https://mine", "PATH=/tmp/evil");

        Assert.True(Settings.TryLoad(null, Env(), out var settings, out _, user));

        Assert.Equal("https://mine", settings.Base);
        Assert.DoesNotContain("PATH", settings.Sources.Keys);
    }

    [Fact]
    public void The_heartbeat_defaults_to_twenty_and_takes_a_number_and_refuses_a_negative_one()
    {
        var origin = Env(("AERIE_BASE", "https://mine"));
        var none = Path.Combine(_temp, "none");

        Assert.True(Settings.TryLoad(null, origin, out var byDefault, out _, none));
        Assert.Equal(20, byDefault.HeartbeatSeconds);

        Assert.True(Settings.TryLoad(
            null, Env(("AERIE_BASE", "https://mine"), ("HATCH_HEARTBEAT", "0")), out var off, out _, none));
        Assert.Equal(0, off.HeartbeatSeconds);

        Assert.True(Settings.TryLoad(
            null, Env(("AERIE_BASE", "https://mine"), ("HATCH_HEARTBEAT", "-5")), out var bad, out _, none));
        Assert.Equal(20, bad.HeartbeatSeconds);
    }

    /// <summary>
    /// A settings file that follows the person rather than the checkout is the
    /// whole point, so it must not be inside one.
    /// </summary>
    [Fact]
    public void The_per_user_path_is_under_the_platforms_application_data_and_is_called_config()
    {
        var path = Settings.UserConfigPath();

        Assert.Equal("config", Path.GetFileName(path));
        Assert.Equal("hatch", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.StartsWith(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
            path);
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
