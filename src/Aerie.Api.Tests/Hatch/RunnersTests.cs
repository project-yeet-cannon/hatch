using Aerie.Api.Modules.Hatch;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// The runner's two horizons, with no database under them: when a row is still
/// being heard from, when it has aged out of the read entirely, and the fact
/// that the second is always ten times the first.
///
/// Worth a file of its own for <see cref="IssueClaimsTests"/>'s reason - the
/// same arithmetic decides what a page draws and what a read returns, and a
/// rule those two disagreed on would be a runner that is gone in one place and
/// absent in the other.
/// </summary>
public class RunnersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    // ---- Here, gone, forgotten ----

    [Fact]
    public void ARunnerHeardFromJustNow_IsHere()
    {
        Assert.True(With().IsHere(Now, Now));
    }

    [Fact]
    public void ARunnerExactlyAtTheCutoff_IsStillHere()
    {
        // The inclusive side, the same one the claim's cutoff is on, so the two
        // never disagree about which side of a boundary an instant is.
        Assert.True(With(60).IsHere(Now.AddSeconds(-60), Now));
    }

    [Fact]
    public void ARunnerOneTickPast_IsGone()
    {
        Assert.False(With(60).IsHere(Now.AddSeconds(-60).AddTicks(-1), Now));
    }

    [Fact]
    public void AGoneRunner_IsStillReturned_UntilTenTimesTheHorizon()
    {
        var runners = With(60);

        // Gone is a thing a row says about itself; dropped is a row that is not
        // there. Between the two there are nine more horizons of a page saying
        // "this loop stopped answering", which is the whole point of having two.
        Assert.False(runners.IsDropped(Now.AddSeconds(-60), Now));
        Assert.False(runners.IsDropped(Now.AddSeconds(-600), Now));
        Assert.True(runners.IsDropped(Now.AddSeconds(-600).AddTicks(-1), Now));
    }

    [Fact]
    public void TheDropHorizon_IsTenTimesTheConfiguredOne()
    {
        // Not independently configurable, and this is what says so: an install
        // that changed the first moved the second with it.
        var runners = With(30);

        Assert.Equal(Now.AddSeconds(-30), runners.Cutoff(Now));
        Assert.Equal(Now.AddSeconds(-300), runners.DropBefore(Now));
    }

    [Fact]
    public void AHorizonOfZero_FallsBackRatherThanBuryingEveryRunner()
    {
        // A misconfigured horizon should cost a fallback, not a page on which
        // every loop is gone the moment it says hello.
        Assert.Equal(new HatchOptions().RunnerGoneAfterSeconds, With(0).GoneAfterSeconds);
        Assert.Equal(new HatchOptions().RunnerGoneAfterSeconds, With(-5).GoneAfterSeconds);
    }

    // ---- What a row draws ----

    [Fact]
    public void ARunnerHoldingNothing_DrawsItsOwnLine()
    {
        var projected = With().Project(Row(line: "nothing on the board is an agent's to move"), null);

        Assert.Null(projected.ClaimKey);
        Assert.Equal("nothing on the board is an agent's to move", projected.Line);
        Assert.Equal(With().GoneAfterSeconds, projected.GoneAfterSeconds);
    }

    [Fact]
    public void ARunnerHoldingATicket_DrawsTheClaimsLineAndNotItsOwn()
    {
        // The row's line is what it said between increments; a runner inside one
        // is already saying things through its lease, and drawing the older of
        // the two would be a card describing the last thing that finished.
        var claim = new ClaimSnapshot(
            Guid.NewGuid(), "aerie-hatch", "here:/tree", Now, Now, "running make test-api", Now);

        var projected = With().Project(Row(line: "resetting the workspace"), ("AER-12", claim));

        Assert.Equal("AER-12", projected.ClaimKey);
        Assert.Equal("running make test-api", projected.Line);
        Assert.Equal(Now, projected.LineAt);
    }

    [Fact]
    public void TheInstruction_IsTheOperatorsHalfOfTheRowAndNothingElse()
    {
        var row = Row();
        row.State = EfHatchRunner.Paused;
        row.Under = "AER-930";
        row.MaxRuns = 12;
        row.MaxSpend = 40m;
        row.UntilAt = Now.AddHours(6);

        var instruction = Runners.Instruct(row);

        Assert.Equal(EfHatchRunner.Paused, instruction.State);
        Assert.Equal("AER-930", instruction.Under);
        Assert.Equal(12, instruction.MaxRuns);
        Assert.Equal(40m, instruction.MaxSpend);
        Assert.Equal(Now.AddHours(6), instruction.UntilAt);
    }

    // ---- The line ----

    [Fact]
    public void ALine_IsItsFirstLineTrimmed()
    {
        Assert.Equal("claimed AER-12", Runners.Normalise("  claimed AER-12  \nand then some\n"));
    }

    [Fact]
    public void AWideLine_IsTruncatedRatherThanRefused()
    {
        var long_ = new string('x', EfHatchRunner.MaxLineLength + 40);

        Assert.Equal(EfHatchRunner.MaxLineLength, Runners.Normalise(long_)!.Length);
    }

    [Fact]
    public void NoLineAndAnEmptyLine_StayDifferentThings()
    {
        // "no opinion" and "clear it" are different instructions all the way
        // through, which is what lets a heartbeat leave a line alone.
        Assert.Null(Runners.Normalise(null));
        Assert.Equal("", Runners.Normalise("   "));
    }

    private static Runners With(int goneAfterSeconds = 90) =>
        new(Options.Create(new HatchOptions { RunnerGoneAfterSeconds = goneAfterSeconds }));

    private static EfHatchRunner Row(string? line = null) => new()
    {
        Name = "here:/tree",
        Kind = EfHatchRunner.LoopKind,
        FirstSeenAt = Now,
        LastSeenAt = Now,
        Line = line,
        LineAt = line is null ? null : Now,
        State = EfHatchRunner.Running,
    };
}
