namespace Hatch.Cli;

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

    /// <summary>
    /// Where the night's running totals are handed from one incarnation of the
    /// loop to the next. Set by the supervisor in <c>hatch.sh</c> and by nobody
    /// else, so its absence is how the runner knows there is no supervisor
    /// standing over it and therefore nothing to restart it.
    /// </summary>
    public string? NightStatePath { get; init; }

    public int OffsetMinutes => Board.OffsetMinutes(Clock.GetLocalNow());

    public Picker Picker() => new(Board, RunnerName, Say);

    public Idle Idle() => new(Board, Say);

    public Increment Increment() => new(Board, Sessions, Settings, Say);

    /// <summary>
    /// This process, on the board: where it says it is alive and reads back
    /// what it has been asked to do. Named by <see cref="RunnerName"/>, which
    /// is the same string its claims carry - a runner has one identity, and the
    /// row is keyed on it.
    /// </summary>
    public Runners Runners() => new(Board.Client, RunnerName);

    /// <summary>
    /// How the tree is made current between increments. Replaceable so a test
    /// can assert the order a pass does things in - the claim, then the reset,
    /// then the spawn - without a remote to fetch from.
    /// </summary>
    public Func<IWorkspace> Workspace { get; init; } = () =>
        throw new InvalidOperationException("no workspace was configured");

    /// <summary>
    /// How the loop reads its own source. Replaceable for the same reason
    /// <see cref="Workspace"/> is: a test says the loop changed underneath
    /// itself without having to change the files it is running from.
    /// </summary>
    public Func<ISelf> Self { get; init; } = () =>
        throw new InvalidOperationException("no source reader was configured");

    /// <summary>The real ones, over this checkout.</summary>
    public Runtime WithGit() => this with
    {
        Workspace = () => new Workspace(Root, Settings.BaseBranch, Say.Line, Say.Complain),
        Self = () => new LoopSource(Root),
    };
}
