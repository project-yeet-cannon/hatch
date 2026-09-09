using Microsoft.Extensions.Options;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The runner, and the one place its two horizons live: how long a row may go
/// unheard from before it reads <em>gone</em>, how much longer before it stops
/// being returned at all, and what a row plus whatever claim it holds looks
/// like on the wire.
///
/// A class rather than a static for the reason <see cref="IssueClaims"/> is
/// one: it holds a configured number and nothing else, and every judgement it
/// makes takes the instant it is judging against rather than reading a clock.
/// </summary>
/// <remarks>
/// Nothing here sweeps and nothing here writes. A runner that stopped answering
/// at midnight is idle at 00:01, gone at 00:02 and not in the answer at all by
/// 00:16 - each of those by arithmetic against <c>LastSeenAt</c> at the moment
/// somebody asks, which is the same lazy expiry the claim uses. There is no
/// timeout to tune and no background job to notice has stopped.
/// </remarks>
public sealed class Runners(IOptions<HatchOptions> options)
{
    /// <summary>
    /// How much longer than <see cref="GoneAfterSeconds"/> a row survives
    /// before it is dropped from the read: ten times, so the shipped default is
    /// gone after a minute and a half and forgotten after fifteen minutes.
    /// </summary>
    /// <remarks>
    /// A constant and not a setting. The two horizons are one judgement seen
    /// twice - "not answering" and "not coming back" - and an installation able
    /// to set them independently could set the second shorter than the first,
    /// which would make a row vanish before it was ever drawn as gone.
    /// </remarks>
    public const int DropMultiple = 10;

    /// <summary>
    /// The silence after which a runner reads as gone, in seconds. Guarded here
    /// and nowhere else: a configured zero or a negative would make every
    /// runner gone on arrival, and a misconfigured horizon should cost a
    /// fallback rather than a page that says every loop is dead.
    /// </summary>
    public int GoneAfterSeconds { get; } =
        options.Value.RunnerGoneAfterSeconds > 0
            ? options.Value.RunnerGoneAfterSeconds
            : new HatchOptions().RunnerGoneAfterSeconds;

    /// <summary>The heartbeat at or after which a runner is still being heard from.</summary>
    public DateTimeOffset Cutoff(DateTimeOffset now) => now.AddSeconds(-GoneAfterSeconds);

    /// <summary>The heartbeat before which a row is not handed out at all.</summary>
    public DateTimeOffset DropBefore(DateTimeOffset now) => now.AddSeconds(-GoneAfterSeconds * DropMultiple);

    /// <summary>
    /// Whether this runner is still answering. Exactly at the cutoff is here;
    /// one tick past it is gone - the same edge the claim draws, so the two
    /// never disagree about which side of a boundary an instant is on.
    /// </summary>
    public bool IsHere(DateTimeOffset lastSeenAt, DateTimeOffset now) => lastSeenAt >= Cutoff(now);

    /// <summary>
    /// Whether this row has aged out of the read. Not a deletion: the row stays
    /// where it is, and a runner that comes back under the same name picks its
    /// own bounds up again rather than being seeded afresh.
    /// </summary>
    public bool IsDropped(DateTimeOffset lastSeenAt, DateTimeOffset now) => lastSeenAt < DropBefore(now);

    /// <summary>
    /// The row as a client draws it, with whatever it holds folded in.
    /// </summary>
    /// <param name="claim">
    /// The live claim this runner holds, or null between increments. The
    /// liveness is the caller's to decide, because a scan judges every row
    /// against one instant - see <see cref="IssueClaims.IsLive"/>.
    /// </param>
    /// <remarks>
    /// The line is the claim's while there is one and the row's when there is
    /// not, and that is the whole of why the row has a line at all: an
    /// increment's chatter already rides its lease, so this column only ever
    /// carries what a runner said between tickets.
    /// </remarks>
    public RunnerDto Project(EfHatchRunner runner, (string Key, ClaimSnapshot Claim)? claim) => new(
        runner.Name,
        runner.Kind,
        runner.FirstSeenAt,
        runner.LastSeenAt,
        claim?.Key,
        claim is { } held ? held.Claim.Chatter : runner.Line,
        claim is { } at ? at.Claim.ChatterAt : runner.LineAt,
        runner.State,
        runner.Under,
        runner.MaxRuns,
        runner.MaxSpend,
        runner.UntilAt,
        GoneAfterSeconds);

    /// <summary>What the loop reads back off its own heartbeat: the row's operator half, and nothing else.</summary>
    public static RunnerInstructionDto Instruct(EfHatchRunner runner) =>
        new(runner.State, runner.Under, runner.MaxRuns, runner.MaxSpend, runner.UntilAt);

    /// <summary>
    /// One line at most, trimmed and capped - <see cref="IssueClaimController"/>'s
    /// rule, called on the other kind of heartbeat. Null stays null, because
    /// "no opinion" and "clear it" are different instructions and the empty
    /// string is the second one.
    /// </summary>
    public static string? Normalise(string? line)
    {
        if (line is null) return null;

        var first = line.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return first.Length > EfHatchRunner.MaxLineLength ? first[..EfHatchRunner.MaxLineLength] : first;
    }
}
