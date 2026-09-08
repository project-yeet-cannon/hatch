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
}
