using Hatch.Api.Modules.Hatch;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// The one property everything downstream leans on: the credential answers
/// "the token, or null", and null is an ordinary value rather than a failure.
/// An installation with no Claude subscription lives here permanently.
/// </summary>
public class ClaudeCredentialTests
{
    [Fact]
    public async Task ReadsTheTokenFromSiteSettings()
    {
        var credential = new SiteSettingClaudeCredential(new StubSiteSettings(claudeSubscriptionToken: "sk-ant-oat-abc"));

        Assert.Equal("sk-ant-oat-abc", await credential.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NullWhenNothingIsSet()
    {
        var credential = new SiteSettingClaudeCredential(new StubSiteSettings());

        Assert.Null(await credential.GetTokenAsync(CancellationToken.None));
    }

    /// <summary>A box somebody cleared, and a value pasted with the newline the clipboard brought along, are both the obvious thing.</summary>
    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  sk-ant-oat-abc\n", "sk-ant-oat-abc")]
    public async Task TrimsAndTreatsBlankAsAbsent(string stored, string? expected)
    {
        var credential = new SiteSettingClaudeCredential(new StubSiteSettings(claudeSubscriptionToken: stored));

        Assert.Equal(expected, await credential.GetTokenAsync(CancellationToken.None));
    }
}
