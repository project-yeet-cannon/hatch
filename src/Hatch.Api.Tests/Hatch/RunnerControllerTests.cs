using Hatch.Api.Modules.Hatch;
using Hatch.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// What the Runner page is told, and what it is handed.
///
/// Every case turns on what is actually on disk, because that is the one thing
/// this controller decides: an image built without the publish step, a partial
/// build, and a whole one all have to produce a page that is true rather than a
/// list of links that 404.
/// </summary>
public sealed class RunnerControllerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hatch-runner-").FullName;

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>Puts a binary where the image would have put it, with bytes worth recognising.</summary>
    private void Publish(string rid, string fileName, string contents = "MZ")
    {
        var directory = Path.Combine(_root, "hatch-runner", rid);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), contents);
    }

    private RunnerController Controller() => new(new StubRevision(Sha), new StubEnvironment(_root));

    /// <summary>
    /// A `dotnet run` from a checkout, and any image built before the publish
    /// step existed. Nothing is offered, and nothing is claimed.
    /// </summary>
    [Fact]
    public void WithNothingPublished_TheListIsEmptyRatherThanFourBrokenLinks()
    {
        var runner = Controller().Get();

        Assert.Empty(runner.Downloads);
        Assert.Equal(Sha, runner.Revision);
    }

    [Fact]
    public void OnlyWhatIsOnDiskIsOffered()
    {
        Publish("win-x64", "hatch.exe");
        Publish("osx-arm64", "hatch");

        var offered = Controller().Get().Downloads;

        Assert.Equal(["win-x64", "osx-arm64"], offered.Select(d => d.Rid));
        Assert.Equal("hatch.exe", offered[0].FileName);
        Assert.Equal("/api/hatch/runner/download/win-x64", offered[0].Url);
    }

    /// <summary>
    /// The whole build. Order is the page's order, and every URL is one the
    /// server named rather than one the page assembled.
    /// </summary>
    [Fact]
    public void AWholeBuildOffersFourPlatformsInOrder()
    {
        foreach (var rid in new[] { "win-x64", "osx-arm64", "osx-x64", "linux-x64" })
            Publish(rid, rid == "win-x64" ? "hatch.exe" : "hatch");

        var offered = Controller().Get().Downloads;

        Assert.Equal(["win-x64", "osx-arm64", "osx-x64", "linux-x64"], offered.Select(d => d.Rid));
        Assert.All(offered, d => Assert.Equal($"/api/hatch/runner/download/{d.Rid}", d.Url));
        Assert.All(offered, d => Assert.False(string.IsNullOrWhiteSpace(d.Platform)));
    }

    /// <summary>
    /// The revision is the image's own, carried on this answer rather than
    /// fetched separately - a page that asked twice could draw a download list
    /// from one build beside a revision from another.
    /// </summary>
    [Fact]
    public void TheRevisionIsTheOneTheseBinariesWereBuiltFrom()
    {
        Publish("linux-x64", "hatch");

        Assert.Equal(Sha, Controller().Get().Revision);
    }

    [Fact]
    public void ADownloadIsTheBytesAndTheNameItLandsOnDiskAs()
    {
        Publish("osx-arm64", "hatch", "#!/not-really");

        var result = Assert.IsType<PhysicalFileResult>(Controller().Download("osx-arm64"));

        Assert.Equal("#!/not-really", File.ReadAllText(result.FileName));
        Assert.Equal("hatch", result.FileDownloadName);
        Assert.Equal("application/octet-stream", result.ContentType);
    }

    [Fact]
    public void WindowsGetsTheNameWindowsNeeds()
    {
        Publish("win-x64", "hatch.exe");

        var result = Assert.IsType<PhysicalFileResult>(Controller().Download("win-x64"));

        Assert.Equal("hatch.exe", result.FileDownloadName);
    }

    [Fact]
    public void APlatformThisBuildDidNotPublishIsNotFound()
    {
        Publish("win-x64", "hatch.exe");

        Assert.IsType<NotFoundResult>(Controller().Download("linux-x64"));
    }

    /// <summary>
    /// The guard is the lookup against the fixed list, not a sanitiser: an
    /// argument that is not one of the four RIDs never reaches Path.Combine, so
    /// there is no path to traverse out of.
    /// </summary>
    [Theory]
    [InlineData("nonsense")]
    [InlineData("../../../etc/passwd")]
    [InlineData("..")]
    [InlineData("win-x64/../../out/Hatch.Api.dll")]
    [InlineData("")]
    public void AnUnrecognisedRidIsNotFound(string rid)
    {
        foreach (var known in new[] { "win-x64", "osx-arm64", "osx-x64", "linux-x64" })
            Publish(known, known == "win-x64" ? "hatch.exe" : "hatch");

        Assert.IsType<NotFoundResult>(Controller().Download(rid));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that will not go is not a failing test.
        }
    }

    private sealed class StubRevision(string revision) : IHatchRevision
    {
        public string Revision { get; } = revision;
        public int Sequence => 1;
        public DateTimeOffset? BuiltAt => null;
        public bool IsDevelopment => Revision == global::Hatch.Api.Services.HatchRevision.Development;
    }

    /// <summary>A web root that is a temp directory, which is the only thing this controller reads out of it.</summary>
    private sealed class StubEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Hatch.Api.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Testing";
    }
}
