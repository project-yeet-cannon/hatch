using Hatch.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Hatch.Api.Tests.AppVersion;

public class AppVersionServiceTests
{
    // Trimmed from a real `vite build` of apps/dashboard - the two hashed
    // assets, plus the third-party stylesheet and the inline bootstrap script
    // that live in the same head and must not affect the result.
    private const string DashboardIndex = """
        <!doctype html>
        <html lang="en">
          <head>
            <link rel="icon" type="image/svg+xml" href="/apps/dashboard/favicon.svg" />
            <link href="https://fonts.googleapis.com/css2?family=Manrope&display=swap" rel="stylesheet">
            <script>fetch('/api/ui-logs', { method: 'POST' });</script>
            <script type="module" crossorigin src="/apps/dashboard/assets/index-BGJobmXl.js"></script>
            <link rel="stylesheet" crossorigin href="/apps/dashboard/assets/index-Dx7nj2H3.css">
          </head>
          <body><div id="root"></div></body>
        </html>
        """;

    [Fact]
    public void ExtractAssetFileNames_ReturnsOnlyHashedAssets()
    {
        var assets = AppVersionService.ExtractAssetFileNames("dashboard", DashboardIndex);

        Assert.Equal(["index-BGJobmXl.js", "index-Dx7nj2H3.css"], assets);
    }

    [Fact]
    public void ExtractAssetFileNames_IgnoresOtherAppsAssets()
    {
        var html = DashboardIndex.Replace(
            "<body>",
            """<body><script src="/apps/admin/assets/index-ZZZZZZZZ.js"></script>""");

        Assert.Equal(
            ["index-BGJobmXl.js", "index-Dx7nj2H3.css"],
            AppVersionService.ExtractAssetFileNames("dashboard", html));
    }

    // The client builds its half of the comparison from DOM order, the server
    // from document order; neither is guaranteed, so both sort.
    [Fact]
    public void ExtractAssetFileNames_IsOrderIndependent()
    {
        var reordered = """
            <link rel="stylesheet" href="/apps/dashboard/assets/index-Dx7nj2H3.css">
            <script type="module" src="/apps/dashboard/assets/index-BGJobmXl.js"></script>
            """;

        Assert.Equal(
            AppVersionService.ExtractAssetFileNames("dashboard", DashboardIndex),
            AppVersionService.ExtractAssetFileNames("dashboard", reordered));
    }

    // A preload/modulepreload alongside the script tag names the same file twice.
    [Fact]
    public void ExtractAssetFileNames_DeduplicatesRepeatedReferences()
    {
        var withPreload = DashboardIndex.Replace(
            "<script type=\"module\"",
            """<link rel="modulepreload" href="/apps/dashboard/assets/index-BGJobmXl.js"><script type="module" """);

        Assert.Equal(
            ["index-BGJobmXl.js", "index-Dx7nj2H3.css"],
            AppVersionService.ExtractAssetFileNames("dashboard", withPreload));
    }

    // Mirrors the "ignores nested paths" case in the dashboard's
    // lib/appVersion.test.ts. A greedy character class captured "chunks" here
    // while the client's URL.pathname parse captured nothing, which would have
    // read as drift that no reload could ever resolve.
    [Fact]
    public void ExtractAssetFileNames_IgnoresNestedAssetPaths()
    {
        var html = """<script type="module" src="/apps/dashboard/assets/chunks/deep-ABC123.js"></script>""";

        Assert.Empty(AppVersionService.ExtractAssetFileNames("dashboard", html));
    }

    // Vite emits neither, but both sides must drop them identically if it ever does.
    [Fact]
    public void ExtractAssetFileNames_IgnoresQueryStringAndFragment()
    {
        var html = """<script src="/apps/dashboard/assets/index-BGJobmXl.js?v=2"></script>""";

        Assert.Equal(["index-BGJobmXl.js"], AppVersionService.ExtractAssetFileNames("dashboard", html));
    }

    [Fact]
    public void ExtractAssetFileNames_ReturnsEmptyForNonViteIndex()
    {
        Assert.Empty(AppVersionService.ExtractAssetFileNames("logo", "<html><body>hand-written</body></html>"));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("dash board")]
    [InlineData("")]
    [InlineData("Dashboard")]
    public void GetVersion_RejectsNamesThatArentAppDirectories(string app)
    {
        var svc = new AppVersionService(new StubWebHostEnvironment());

        Assert.Null(svc.GetVersion(app));
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = nameof(AppVersionServiceTests);
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Test";
    }
}
