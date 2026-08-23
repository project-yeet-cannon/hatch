using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Game;

/// <summary>
/// One game being built. A world is its history: the code it runs today is just
/// whichever version is current, and every version anyone ever played is still
/// here.
/// </summary>
/// <remarks>
/// Keeping every version rather than a single mutable blob is what makes undo
/// possible, and undo is not a nicety here - the player is four, the author is
/// a language model, and between them a turn will eventually produce something
/// worse than what it replaced. Going back has to be one tap and it has to be
/// free.
/// </remarks>
[Table("Worlds")]
public class GameWorld
{
    public const int MaxNameLength = 60;
    public const int MaxIconLength = 32;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [MaxLength(MaxNameLength)]
    public required string Name { get; set; }

    /// <summary>An emoji, shown on the worlds screen. Matches the family shell's module icons.</summary>
    [MaxLength(MaxIconLength)]
    public string? Icon { get; set; }

    /// <summary>
    /// Which version is live. Deliberately a bare Guid rather than a navigation
    /// property: a world points at a version and every version points back at
    /// its world, and modelling both ends gives EF a cycle to argue about for
    /// no gain - nothing here ever loads the current version except by id.
    /// </summary>
    public Guid? CurrentVersionId { get; set; }

    /// <summary>
    /// Personal bests, as the JSON the engine hands up (see GameRecordDto). It
    /// lives on the world rather than the version because a record belongs to
    /// the child, not to a build - rewriting the game does not un-jump the
    /// longest jump.
    /// </summary>
    public string RecordsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last turn, revert, or record - what orders the worlds screen.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public List<GameVersion> Versions { get; set; } = [];
}

/// <summary>Why a version exists, which is most of what the history screen shows.</summary>
public enum GameVersionKind
{
    /// <summary>The empty world every game starts from, written by no one.</summary>
    Seed = 0,

    /// <summary>Someone asked for something.</summary>
    Turn = 1,

    /// <summary>The previous version crashed and the model was sent the error.</summary>
    Repair = 2,

    /// <summary>A copy of an older version, made current again.</summary>
    Revert = 3,
}

/// <summary>
/// One state of a game's code. Immutable once written, apart from the two
/// broken-* fields, which the client sets when the code it was handed threw.
/// </summary>
[Table("Versions")]
[Index(nameof(WorldId), nameof(Ordinal), IsUnique = true)]
public class GameVersion
{
    public const int MaxPromptLength = 500;
    public const int MaxSummaryLength = 300;
    public const int MaxModelLength = 60;
    public const int MaxErrorLength = 1000;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid WorldId { get; set; }
    public GameWorld? World { get; set; }

    /// <summary>1, 2, 3... within the world. What the history screen counts and what "undo" walks back along.</summary>
    public int Ordinal { get; set; }

    public GameVersionKind Kind { get; set; }

    /// <summary>What the player typed, when a player typed something.</summary>
    [MaxLength(MaxPromptLength)]
    public string? Prompt { get; set; }

    /// <summary>The game itself. No length cap - this is the artifact.</summary>
    public required string Code { get; set; }

    /// <summary>The model's one-sentence answer to the child.</summary>
    [MaxLength(MaxSummaryLength)]
    public string? Summary { get; set; }

    /// <summary>The mechanic it slipped in unasked, when it added one.</summary>
    [MaxLength(MaxSummaryLength)]
    public string? Extra { get; set; }

    [MaxLength(MaxModelLength)]
    public string? Model { get; set; }

    /// <summary>Which version this was written from, so the history is a chain rather than a list.</summary>
    public Guid? ParentVersionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Wall-clock time the model took, in milliseconds. The only honest answer to "why is it slow".</summary>
    public int DurationMs { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    /// <summary>
    /// Tokens served from cache rather than re-read. Worth storing next to the
    /// other two: the engine reference is most of every request, and a run of
    /// zeroes here is the symptom of a cache that silently stopped working.
    /// </summary>
    public int CachedInputTokens { get; set; }

    /// <summary>
    /// When the running game threw, reported by the frame that was playing it.
    /// A version with this set is never reverted to and never used as the
    /// parent of a new turn - it is the one state known to be worse than
    /// nothing.
    /// </summary>
    public DateTimeOffset? BrokenAt { get; set; }

    [MaxLength(MaxErrorLength)]
    public string? BrokenError { get; set; }
}
