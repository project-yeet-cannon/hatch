using System.Net;
using System.Text;

namespace Hatch.Cli.Tests;

/// <summary>
/// <c>runner-claude-token</c> - the one call the container's entrypoint makes
/// before it starts the loop.
/// </summary>
/// <remarks>
/// The exit codes carry the meaning here, because the caller is a POSIX shell
/// with no JSON in it: <c>0</c> and a token, <c>2</c> for a Hatch holding none
/// yet, <c>1</c> for everything else. Each is asserted, and so is the property
/// the whole design rests on - that nothing but the token itself reaches
/// standard output.
/// </remarks>
public sealed class ClaudeTokenCommandTests
{
    [Fact]
    public async Task A_wrapped_token_is_printed_as_itself_and_nothing_else()
    {
        using var h = new CliHarness();
        Answer(h, Protected("sk-ant-oat-1"));

        Assert.Equal(0, await new ClaudeTokenCommand(h.Cli).RunAsync([], default));
        Assert.Equal(["sk-ant-oat-1"], h.Say.Said);
        Assert.Empty(h.Say.Complained);
    }

    /// <summary>
    /// The untagged form <c>SecretProtector.Unprotect</c> still reads - a value
    /// written before the scheme tag existed. Read the same way here, so a
    /// container is not the one caller that a data migration would have broken.
    /// </summary>
    [Fact]
    public async Task An_untagged_payload_is_read_as_the_scheme_it_is()
    {
        using var h = new CliHarness();
        Answer(h, Obfuscated("sk-ant-oat-1"));

        Assert.Equal(0, await new ClaudeTokenCommand(h.Cli).RunAsync([], default));
        Assert.Equal(["sk-ant-oat-1"], h.Say.Said);
    }

    /// <summary>
    /// Criterion 4's other half: no token yet is an ordinary state, told apart
    /// from a fault by its own code so the entrypoint can wait rather than
    /// give up.
    /// </summary>
    [Fact]
    public async Task No_token_saved_is_its_own_exit_code_and_says_nothing()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/settings/claude-token", HttpStatusCode.NoContent);

        Assert.Equal(ClaudeTokenCommand.NoToken, await new ClaudeTokenCommand(h.Cli).RunAsync([], default));
        Assert.Empty(h.Say.Said);
        Assert.Empty(h.Say.Complained);
    }

    /// <summary>A Hatch with its wall on refuses this route outright, and the refusal is worth reading.</summary>
    [Fact]
    public async Task A_refusal_is_complained_about_and_exits_one()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/settings/claude-token", HttpStatusCode.Forbidden);

        Assert.Equal(1, await new ClaudeTokenCommand(h.Cli).RunAsync([], default));
        Assert.Contains("403", h.Complained);
        Assert.Empty(h.Say.Said);
    }

    /// <summary>
    /// A Hatch too old to have the route at all. The one thing that must not
    /// happen is the entrypoint reading this as "no token yet" and waiting all
    /// night for one that is already set.
    /// </summary>
    [Fact]
    public async Task A_hatch_without_the_route_exits_one_rather_than_two()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new ClaudeTokenCommand(h.Cli).RunAsync([], default));
        Assert.Contains("404", h.Complained);
        Assert.Empty(h.Say.Said);
    }

    /// <summary>
    /// A payload written under a scheme this build has never heard of - a
    /// rolled-back runner reading a newer Hatch. Unreadable is the honest
    /// answer, and it is a fault rather than an empty line on stdout, which is
    /// what an entrypoint would have exported as a token.
    /// </summary>
    [Fact]
    public async Task A_payload_this_build_cannot_read_exits_one_and_prints_nothing()
    {
        using var h = new CliHarness();
        Answer(h, "v2:" + Obfuscated("sk-ant-oat-1"));

        Assert.Equal(1, await new ClaudeTokenCommand(h.Cli).RunAsync([], default));
        Assert.Empty(h.Say.Said);
        Assert.Contains("cannot read", h.Complained);
    }

    [Fact]
    public async Task A_body_that_is_not_a_token_exits_one()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/settings/claude-token", HttpStatusCode.OK, "not json at all");

        Assert.Equal(1, await new ClaudeTokenCommand(h.Cli).RunAsync([], default));
        Assert.Empty(h.Say.Said);
    }

    [Fact]
    public async Task An_argument_is_refused_and_calls_nothing()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new ClaudeTokenCommand(h.Cli).RunAsync(["AER-12"], default));
        Assert.Contains("takes no arguments", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task Dash_h_prints_its_own_usage_and_calls_nothing()
    {
        using var h = new CliHarness();

        Assert.Equal(0, await new ClaudeTokenCommand(h.Cli).RunAsync(["-h"], default));
        Assert.StartsWith("usage: hatch runner-claude-token", h.Said);
        Assert.Empty(h.Wire.Calls);
    }

    private static void Answer(CliHarness h, string protectedToken) =>
        h.Wire.Reply(
            "GET", "/api/hatch/settings/claude-token", HttpStatusCode.OK,
            System.Text.Json.JsonSerializer.Serialize(new ClaudeTokenDto(protectedToken), Fixtures.Json));

    /// <summary>
    /// What the server writes, spelled out here rather than reversed through
    /// the code under test: a test that obfuscated with <c>Secret</c>'s own key
    /// would pass just as happily if that key had drifted from the API's.
    /// </summary>
    private static string Protected(string plaintext) => "v1:" + Obfuscated(plaintext);

    private static string Obfuscated(string plaintext)
    {
        var key = "Hatch.SiteSettings.Obfuscation.v1"u8.ToArray();
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= key[i % key.Length];
        return Convert.ToBase64String(bytes);
    }
}
