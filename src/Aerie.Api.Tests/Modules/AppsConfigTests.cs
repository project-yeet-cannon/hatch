using Aerie.Api.Modules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Tests.Modules;

/// <summary>
/// Covers the one operator value the family shell reads. It gets its own tests
/// because its only other feedback loop is a printed QR label that opens
/// nothing, discovered at a box, months later.
/// </summary>
public class AppsConfigTests
{
    [Theory]
    [InlineData("https://home.example.com", "https://home.example.com")]
    [InlineData("https://home.example.com/", "https://home.example.com")]
    [InlineData("  https://home.example.com//  ", "https://home.example.com")]
    [InlineData("http://192.168.1.10:8080", "http://192.168.1.10:8080")]
    // A path prefix survives: some installs sit behind a proxy on a sub-path.
    [InlineData("https://example.com/aerie", "https://example.com/aerie")]
    public void NormalizeBaseUrl_KeepsUsableValues_WithoutATrailingSlash(string configured, string expected)
        => Assert.Equal(expected, AppsOptions.NormalizeBaseUrl(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("home.example.com")]     // no scheme - a camera has nothing to open
    [InlineData("/apps/family")]         // relative
    [InlineData("ftp://home.example.com")]
    public void NormalizeBaseUrl_RejectsWhatCannotBeScanned(string? configured)
        => Assert.Null(AppsOptions.NormalizeBaseUrl(configured));

    [Fact]
    public void GetConfig_ReportsNull_WhenUnconfigured()
        => Assert.Null(Controller("").GetConfig().PublicBaseUrl);

    [Fact]
    public void GetConfig_ReportsTheNormalizedValue()
        => Assert.Equal("https://home.example.com", Controller("https://home.example.com/").GetConfig().PublicBaseUrl);

    /// <summary>Set-but-unusable degrades to null rather than shipping a broken base to the printer.</summary>
    [Fact]
    public void GetConfig_ReportsNull_WhenConfiguredValueIsUnusable()
        => Assert.Null(Controller("home.example.com").GetConfig().PublicBaseUrl);

    private static AppsController Controller(string publicBaseUrl) =>
        new(Options.Create(new AppsOptions { PublicBaseUrl = publicBaseUrl }), NullLogger<AppsController>.Instance);
}
