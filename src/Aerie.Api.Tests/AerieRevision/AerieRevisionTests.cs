using Aerie.Api.Models.AerieRevision;
using Aerie.Api.Services;

namespace Aerie.Api.Tests.AerieRevision;

/// <summary>
/// The stamp read back out of the assembly, and the ordering rule a wall
/// tablet's reload decision rests on. See docs/plans/version.md.
/// </summary>
public class AerieRevisionTests
{
    private const string Sha = "b465267d5809bc0c672d250a1e7cc81b8714ed92";
    private const string OtherSha = "2d868dcb2fb27834c99fbb1c3805c6e7f882f170";

    [Fact]
    public void ParseRevision_ReadsTheShaTheSdkAppends()
    {
        Assert.Equal(Sha, Api.Services.AerieRevision.ParseRevision($"1.0.0+{Sha}"));
    }

    [Theory]
    // A local `dotnet build`, which passes no SourceRevisionId at all.
    [InlineData("1.0.0")]
    [InlineData(null)]
    [InlineData("")]
    // A '+' with nothing after it.
    [InlineData("1.0.0+")]
    // The 7-character short sha the image tags carried before this existed -
    // rejected rather than accepted, so a half-finished migration to the full
    // sha shows up as "dev" instead of as a value that silently never matches.
    [InlineData("1.0.0+b465267")]
    // Not hex.
    [InlineData("1.0.0+zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ParseRevision_DegradesToDevelopment(string? informationalVersion)
    {
        Assert.Equal("dev", Api.Services.AerieRevision.ParseRevision(informationalVersion));
    }

    [Fact]
    public void ParseSequence_ReadsAPositiveCount()
    {
        Assert.Equal(4127, Api.Services.AerieRevision.ParseSequence("4127"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    // 0 and negatives are indistinguishable from "unstamped" to every caller,
    // so they collapse to the same answer rather than becoming a second case.
    [InlineData("0")]
    [InlineData("-3")]
    public void ParseSequence_DegradesToZero(string? value)
    {
        Assert.Equal(0, Api.Services.AerieRevision.ParseSequence(value));
    }

    [Fact]
    public void ARealAssemblyAlwaysReadsAsEitherAShaOrDev()
    {
        // Not "the test assembly is a dev build" - it usually isn't. The SDK's
        // built-in SourceLink sets SourceRevisionId from git on its own, so a
        // build made inside a checkout is stamped with the full sha whether or
        // not anyone asked. That is correct and useful: `make run` reports the
        // commit it is actually running.
        //
        // Where it does *not* happen is inside the container, because
        // .dockerignore excludes .git - which is exactly the case Dockerfile.api
        // passes the build arg for. Both paths land here, so the invariant
        // worth asserting is that neither can produce a third kind of answer.
        var revision = new Api.Services.AerieRevision(typeof(AerieRevisionTests).Assembly);

        Assert.True(
            revision.Revision == "dev" || System.Text.RegularExpressions.Regex.IsMatch(revision.Revision, "^[0-9a-f]{40}$"),
            $"unexpected revision '{revision.Revision}'");
        Assert.Equal(revision.Revision == "dev", revision.IsDevelopment);
    }

    [Fact]
    public void Compare_SameShaIsCurrent()
    {
        Assert.Equal(RevisionDrift.Current, RevisionComparison.Compare(Sha, 4127, Sha, 4127));
    }

    [Fact]
    public void Compare_SameShaIsCurrentEvenWhenTheCountsDisagree()
    {
        // Two builds of one commit are one build. A count that disagrees is a
        // build-pipeline bug, and reloading a tablet over it would fix nothing.
        Assert.Equal(RevisionDrift.Current, RevisionComparison.Compare(Sha, 4127, Sha, 9));
    }

    [Fact]
    public void Compare_LowerCountIsBehind()
    {
        Assert.Equal(RevisionDrift.Behind, RevisionComparison.Compare(Sha, 4127, OtherSha, 4126));
    }

    [Fact]
    public void Compare_HigherCountIsAhead()
    {
        // The case the enum exists for: mid-rolling-deploy a browser loaded
        // from a new replica asks an old one. A client that reloaded on any
        // difference would thrash until the rollout finished.
        Assert.Equal(RevisionDrift.Ahead, RevisionComparison.Compare(Sha, 4126, OtherSha, 4127));
    }

    [Theory]
    // Unstamped on the server side...
    [InlineData("dev", 0, OtherSha, 4127)]
    // ...on the client side...
    [InlineData(Sha, 4127, "dev", 0)]
    // ...or on both, which is the one that bites: "dev" == "dev" is a string
    // match and not a fact, and reporting Current there would tell a developer
    // their page agreed with a server it has never agreed with.
    [InlineData("dev", 0, "dev", 0)]
    // A sha present but no usable count - a client from before the sequence
    // existed. There is an identity but no position, so no ordering.
    [InlineData(Sha, 4127, OtherSha, 0)]
    public void Compare_UnstampedIsUnknownRatherThanCurrent(
        string revision, int sequence, string clientRevision, int clientSequence)
    {
        // Folding this into Current would make `npm run dev` look up to date,
        // which is the one place a false "current" is guaranteed to be wrong.
        Assert.Equal(
            RevisionDrift.Unknown,
            RevisionComparison.Compare(revision, sequence, clientRevision, clientSequence));
    }

    [Fact]
    public void Compare_EqualCountsWithDifferentShasIsUnknown()
    {
        // Divergent history - two branches at the same depth. There is no
        // ordering here to find, and picking one would be a guess.
        Assert.Equal(RevisionDrift.Unknown, RevisionComparison.Compare(Sha, 4127, OtherSha, 4127));
    }

    [Fact]
    public void Compare_IsCaseInsensitiveOnTheSha()
    {
        Assert.Equal(RevisionDrift.Current, RevisionComparison.Compare(Sha, 4127, Sha.ToUpperInvariant(), 4127));
    }
}
