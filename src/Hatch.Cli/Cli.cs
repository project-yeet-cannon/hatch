namespace Hatch.Cli;

/// <summary>
/// Everything a conversational command needs, which is less than an increment
/// needs.
/// </summary>
/// <remarks>
/// <see cref="Runtime"/> carries a checkout, a session runner, a workspace and a
/// clock, because <c>work</c> and <c>go-to-work</c> spawn an agent in a tree.
/// The other fourteen commands make one request and say a sentence about the
/// answer: they have no tree, and requiring one is exactly the thing AERIE-934
/// is about undoing. So they take this instead, and <c>hatch board</c> works in
/// a directory that has never been a repository.
/// </remarks>
/// <param name="RunnerName">
/// What a keyless call named itself, which <c>config --show</c> prints so that
/// somebody can see what a wall-off Hatch will record.
/// </param>
public sealed record Cli(
    Board Board,
    Terminal Say,
    Input In,
    Settings Settings,
    string RunnerName)
{
    /// <summary>The clock, so a test can put a ready date on either side of today.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public DateTimeOffset Now => Clock.GetLocalNow();

    public int OffsetMinutes => Board.OffsetMinutes(Now);

    /// <summary>
    /// Where a question is read and answered in a browser. Hatch's own origin,
    /// where "/" is the board - so a question printed here is a link somebody
    /// can follow, which is the whole point of putting it on the ticket instead
    /// of leaving it in a scrollback.
    /// </summary>
    public string IssueUrl(string key) => $"{Board.Client.Origin}/issues/{key}";
}
