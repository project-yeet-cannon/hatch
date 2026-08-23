namespace Aerie.Api.Modules.Game;

// The API-facing shapes for the game module. Entities stay behind these: the
// frame that plays a game needs code and records and nothing else, and the
// screens around it need history without code.

/// <summary>A game on the worlds screen - everything but the code.</summary>
public record WorldSummaryDto(
    Guid Id, string Name, string? Icon, int VersionCount, string? LastPrompt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// A game as the play screen needs it: what to run, what the bests are, and
/// which turn produced this. Code travels here rather than on VersionDto so the
/// history list stays small enough to poll.
/// </summary>
public record WorldDto(
    WorldSummaryDto World, VersionDto? Current, string Code, IReadOnlyList<GameRecordDto> Records);

/// <summary>One turn in a world's history.</summary>
public record VersionDto(
    Guid Id, int Ordinal, GameVersionKind Kind, string? Prompt, string? Summary, string? Extra,
    string? Model, DateTimeOffset CreatedAt, int DurationMs, int InputTokens, int OutputTokens,
    int CachedInputTokens, bool IsBroken);

/// <summary>
/// A personal best, exactly as the engine keeps it. The server stores these
/// without interpreting them - the id and label are the game's own invention,
/// and the day it invents "highest bounce" nothing here should need changing.
/// </summary>
public record GameRecordDto(string Id, string Label, double Value, string? Unit, bool LowerIsBetter);

public record WorldWriteRequest(string Name, string? Icon);

/// <summary>
/// A player asking for something.
/// </summary>
/// <param name="Prompt">What they typed.</param>
/// <param name="Model">
/// Which model writes it - see <see cref="GameModelChoice"/>. Named for what it
/// costs the player (a wait) rather than for a model id, and mapped server-side,
/// so the browser can never name a model and no price list ships to the client.
/// </param>
public record TurnRequest(string Prompt, GameModelChoice? Model);

/// <summary>What the two speeds mean. The whole client-facing model vocabulary.</summary>
public enum GameModelChoice
{
    /// <summary>The default: a smaller model, back in seconds. Right for nearly every turn.</summary>
    Quick = 0,

    /// <summary>The bigger model, for the ask that keeps coming back wrong.</summary>
    Careful = 1,
}

/// <summary>
/// The frame reporting that the code it was given threw. Carries the version id
/// so a stale frame - one still running yesterday's version in a background tab
/// - cannot condemn today's.
/// </summary>
public record BreakageReport(Guid VersionId, string Phase, string Message, string? Stack);

public record RevertRequest(Guid VersionId);

/// <summary>The engine's records on their way to being stored, sent whenever one is beaten.</summary>
public record RecordsWriteRequest(IReadOnlyList<GameRecordDto> Records);

/// <summary>
/// Whether this install can author games at all, so the play screen can say
/// "an adult needs to add a key on the Settings page" instead of failing at the
/// first thing a child types.
/// </summary>
public record GameCapabilityDto(bool CanAuthor, string? Reason);
