namespace Aerie.Hatch;

/// <summary>
/// Everything a command needs that is not one of its own flags: where Hatch is,
/// where the tree is, what this runner is called, and how to spawn a session.
/// </summary>
/// <param name="Heartbeat">
/// How often a claim says it is still here. Null takes the server's TTL, which
/// is what every real run does; a test sets it so a heartbeat happens inside a
/// test rather than in five minutes' time.
/// </param>
public sealed record Runtime(
    Settings Settings,
    Board Board,
    ISessionRunner Sessions,
    Terminal Say,
    string Root,
    string RunnerName,
    string TempDirectory,
    TimeSpan? Heartbeat = null)
{
    /// <summary>The clock, so a test can put the loop at a particular hour.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public int OffsetMinutes => Board.OffsetMinutes(Clock.GetLocalNow());

    public Picker Picker() => new(Board, RunnerName, Say);

    public Idle Idle() => new(Board, Say);

    public Increment Increment() => new(Board, Sessions, Settings, Say);

    /// <summary>
    /// How the tree is made current between increments. Replaceable so a test
    /// can assert the order a pass does things in - the claim, then the reset,
    /// then the spawn - without a remote to fetch from.
    /// </summary>
    public Func<IWorkspace> Workspace { get; init; } = () =>
        throw new InvalidOperationException("no workspace was configured");

    /// <summary>The real one, over this checkout.</summary>
    public Runtime WithGit() => this with
    {
        Workspace = () => new Workspace(Root, Settings.BaseBranch, Say.Line, Say.Complain),
    };
}
