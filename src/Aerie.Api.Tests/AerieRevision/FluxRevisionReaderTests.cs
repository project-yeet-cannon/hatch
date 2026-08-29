using Aerie.Api.Services;

namespace Aerie.Api.Tests.AerieRevision;

/// <summary>
/// Parsing Flux's revision strings. The shapes here were read off the live
/// cluster rather than invented - see docs/plans/version.md, finding 8.
/// </summary>
public class FluxRevisionReaderTests
{
    [Fact]
    public void ParseRevision_ReadsTheCurrentFluxForm()
    {
        var (branch, sha) = FluxRevisionReader.ParseRevision("main@sha1:b465267d5809bc0c672d250a1e7cc81b8714ed92");

        Assert.Equal("main", branch);
        Assert.Equal("b465267d5809bc0c672d250a1e7cc81b8714ed92", sha);
    }

    [Fact]
    public void ParseRevision_ReadsTheOlderSlashForm()
    {
        // Accepted so that upgrading the Flux controllers doesn't silently
        // blank the field out - the symptom would be an admin screen that
        // stops showing a revision, with nothing logged anywhere.
        var (branch, sha) = FluxRevisionReader.ParseRevision("main/b465267d5809bc0c672d250a1e7cc81b8714ed92");

        Assert.Equal("main", branch);
        Assert.Equal("b465267d5809bc0c672d250a1e7cc81b8714ed92", sha);
    }

    [Fact]
    public void ParseRevision_HandlesABranchNameContainingASlash()
    {
        var (branch, sha) = FluxRevisionReader.ParseRevision("release/2026-08@sha1:2d868dcb2fb27834c99fbb1c3805c6e7f882f170");

        Assert.Equal("release/2026-08", branch);
        Assert.Equal("2d868dcb2fb27834c99fbb1c3805c6e7f882f170", sha);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseRevision_IsNullForAResourceThatHasNeverReconciled(string? revision)
    {
        var (branch, sha) = FluxRevisionReader.ParseRevision(revision);

        Assert.Null(branch);
        Assert.Null(sha);
    }

    [Fact]
    public async Task ReadAsync_ReportsUnavailableOutsideACluster()
    {
        // The path every local run takes - `make run`, compose, this test host.
        // It must degrade in band rather than throw: the endpoint's whole job is
        // to answer when things are not well.
        var reader = new FluxRevisionReader(
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<FluxRevisionReader>(),
            TimeProvider.System);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Empty(result.Sources);
        Assert.NotNull(result.Unavailable);
    }
}
