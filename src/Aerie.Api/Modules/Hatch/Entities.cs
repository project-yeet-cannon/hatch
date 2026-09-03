using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// A project, which is really just a key namespace: it exists so that issues
/// can be called <c>AER-12</c> instead of <c>#4471</c>, and so that two efforts
/// can number themselves independently.
///
/// It is deliberately not a container. The board (docs/plans/pjm.md, "Goals")
/// shows every issue from every project at once, because the operator has one
/// pair of hands and switching boards to find out what is next is the thing
/// markdown files already do badly.
/// </summary>
[Table("Projects")]
[Index(nameof(Key), IsUnique = true)]
public class EfHatchProject
{
    /// <summary>
    /// <c>AER</c>, <c>OPS</c>. Upper case, starts with a letter, two to six
    /// characters - short because it is typed into a chat window and read in a
    /// card corner.
    /// </summary>
    public const string KeyPattern = "^[A-Z][A-Z0-9]{1,5}$";

    public const int MaxKeyLength = 6;
    public const int MaxNameLength = 120;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>
    /// The prefix of every issue key in this project, and immutable after
    /// creation. Renaming it would silently orphan every <c>AER-12</c> written
    /// into a commit message, a chat log, or a branch name - references this
    /// database has never seen and cannot rewrite - so the API refuses the edit
    /// rather than offering a rename that only half works.
    /// </summary>
    [MaxLength(MaxKeyLength)]
    public required string Key { get; set; }

    [MaxLength(MaxNameLength)]
    public required string Name { get; set; }

    /// <summary>
    /// The number the next issue in this project will take. Kept as a column
    /// rather than derived from <c>MAX(Number) + 1</c> because a deleted issue
    /// must not hand its number to the next one: <c>AER-12</c> in an old chat
    /// log should be a dead link, never a different ticket.
    /// </summary>
    /// <remarks>
    /// <see cref="ConcurrencyCheckAttribute"/> is what makes the mint safe
    /// without a lock or a sequence. Two concurrent creates both read 12, and
    /// the second <c>SaveChanges</c> finds the row no longer holding what it
    /// read and throws <c>DbUpdateConcurrencyException</c> - which the create
    /// path catches and retries (docs/plans/pjm.md, "Issue numbering"). The
    /// unique index on <c>(ProjectId, Number)</c> is the backstop underneath,
    /// so the worst case is a refused request rather than two issues wearing
    /// the same key.
    /// </remarks>
    [ConcurrencyCheck]
    public int NextIssueNumber { get; set; } = 1;

    public required DateTimeOffset CreatedAt { get; set; }

    public ICollection<EfHatchIssue> Issues { get; set; } = [];

    public static bool IsValidKey(string? key) =>
        key is not null && Regex.IsMatch(key, KeyPattern, RegexOptions.None, TimeSpan.FromSeconds(1));
}

/// <summary>
/// One column on the board. Global rather than per-project - the board shows
/// every project at once, so a per-project status set would have no column to
/// put a foreign issue in.
///
/// Rows rather than an enum because the operator reorders and renames them from
/// the Statuses page, and an enum would make "add a review column" a deploy.
/// </summary>
[Table("Statuses")]
[Index(nameof(Name), IsUnique = true)]
public class EfHatchStatus
{
    public const int MaxNameLength = 60;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [MaxLength(MaxNameLength)]
    public required string Name { get; set; }

    /// <summary>
    /// Left-to-right column order. Sparse on purpose (the seed is 10/20/30/40)
    /// so inserting a column between two is a single write rather than a
    /// renumber - the same trick <see cref="EfHatchIssue.Rank"/> plays a size
    /// up.
    /// </summary>
    public required int SortOrder { get; set; }

    /// <summary>
    /// Whether landing here means the work shipped. Nothing in the MVP reads
    /// this for behaviour except the importer (a checked box lands terminal);
    /// it is carried because "how much did we finish in March" is a query
    /// against the event log plus this flag, and a flag added later would be
    /// blank for every row that already exists.
    /// </summary>
    public bool IsTerminal { get; set; }
}

/// <summary>
/// A unit of work - an epic, a story, a task, or a bug. The board's card, the
/// detail page's subject, and the thing Claude is handed a link to.
/// </summary>
/// <remarks>
/// The display key (<c>AER-12</c>) is deliberately not a column. It is
/// <c>Project.Key + "-" + Number</c>, computed at the edge, so there is exactly
/// one fact about a key stored anywhere and no chance of the two disagreeing
/// after a project rename that should never have been allowed anyway.
/// </remarks>
[Table("Issues")]
// The numbering backstop: whatever the retry loop in the create path does, the
// database will not hold two AER-12s.
[Index(nameof(ProjectId), nameof(Number), IsUnique = true)]
// The board's one query - every issue, ordered by column then rank.
[Index(nameof(StatusId), nameof(Rank))]
public class EfHatchIssue
{
    public const int MaxTitleLength = 300;
    public const int MaxDescriptionLength = 200_000;
    public const int MaxTypeLength = 16;

    /// <summary>The four types, in the order a picker should offer them.</summary>
    public static readonly string[] Types = ["epic", "story", "task", "bug"];

    /// <summary>
    /// Which parents each type may take. Advisory shape rather than a
    /// hierarchy: every issue may also have no parent at all, which is the
    /// ordinary state of a freshly captured bug.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> LegalParentTypes =
        new Dictionary<string, string[]>
        {
            ["epic"] = ["epic"],
            ["story"] = ["epic"],
            ["task"] = ["story", "bug"],
            ["bug"] = ["epic", "story"],
        };

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public required int ProjectId { get; set; }
    public EfHatchProject? Project { get; set; }

    /// <summary>The <c>12</c> in <c>AER-12</c>, serial within the project and never reused.</summary>
    public required int Number { get; set; }

    [MaxLength(MaxTypeLength)]
    public required string Type { get; set; }

    [MaxLength(MaxTitleLength)]
    public required string Title { get; set; }

    /// <summary>
    /// Markdown, stored exactly as typed. Rendered by the client
    /// (<c>marked</c> + <c>dompurify</c>, the pair the docs app already
    /// bundles), never by the server - so what the database holds is what
    /// somebody wrote, and a change of renderer is a frontend change.
    /// </summary>
    public string Description { get; set; } = "";

    public required int StatusId { get; set; }
    public EfHatchStatus? Status { get; set; }

    /// <summary>
    /// The epic above this story, or the story above this task. Nullable and
    /// usually null; validated server-side for existence, same project, legal
    /// type pairing, and no cycles.
    /// </summary>
    public long? ParentId { get; set; }
    public EfHatchIssue? Parent { get; set; }
    public ICollection<EfHatchIssue> Children { get; set; } = [];

    /// <summary>
    /// Position within the column. Sparse integers with 1024-sized gaps, so a
    /// drop between two cards is the midpoint and costs one UPDATE; when the
    /// gap closes the whole column is renumbered (see <c>RankService</c>).
    /// Lexorank strings were considered and rejected - string midpoint maths
    /// has sharp edges, and a column here holds tens of cards, not millions.
    /// </summary>
    public required long Rank { get; set; }

    /// <summary>
    /// The day this becomes workable, and nothing about it before then. An
    /// issue whose <c>ReadyAt</c> is in the future is folded off the board
    /// (<c>BoardPage</c>), which is what lets a ticket be filed the moment it is
    /// thought of rather than the moment it can be started: buy a certificate in
    /// September and the renewal appears next August on its own.
    /// </summary>
    /// <remarks>
    /// A gate, not a schedule. Nothing refuses to move a card that is not ready
    /// yet, and nothing checks this against <see cref="DueAt"/> - an issue ready
    /// after it is due is a mistake worth seeing on screen, not one worth a 400.
    /// </remarks>
    public DateTimeOffset? ReadyAt { get; set; }

    /// <summary>Whether <see cref="ReadyAt"/>'s time of day was meant - see <see cref="IssueMoment"/>.</summary>
    public bool ReadyAtHasTime { get; set; }

    /// <summary>
    /// When it is owed. Drawn on the card as a chip that warms as the date
    /// approaches, and the field the eventual automatic prioritisation will
    /// sort on.
    /// </summary>
    /// <remarks>
    /// A date in the past is accepted without comment. Half of what a tracker
    /// is for is recording that something was due last Tuesday, and a form that
    /// argues about it is a form people stop telling the truth to.
    /// </remarks>
    public DateTimeOffset? DueAt { get; set; }

    /// <summary>Whether <see cref="DueAt"/>'s time of day was meant - see <see cref="IssueMoment"/>.</summary>
    public bool DueAtHasTime { get; set; }

    /// <summary>
    /// Who filed it, as a name rather than a foreign key. The audit trail wants
    /// to read the same after a person row is deleted, and Phase 6 puts API key
    /// names in this column beside human ones - neither of which a
    /// <c>People</c> FK from a module schema could express (Modules/README.md).
    /// </summary>
    [MaxLength(Common.PersonName.MaxChars)]
    public required string CreatedBy { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }

    public ICollection<EfHatchComment> Comments { get; set; } = [];
    public ICollection<EfHatchIssueEvent> Events { get; set; } = [];

    public static bool IsValidType(string? type) => type is not null && Types.Contains(type);
}

/// <summary>A comment on an issue. Markdown, like the description, and rendered the same way.</summary>
[Table("Comments")]
[Index(nameof(IssueId), nameof(CreatedAt))]
public class EfHatchComment
{
    public const int MaxBodyLength = 100_000;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>
    /// Not <c>required</c>, unlike most of what a row needs: a comment added
    /// through <see cref="EfHatchIssue.Comments"/> has this filled in by EF's
    /// fixup, which is what lets an issue and its first rows be written in one
    /// <c>SaveChanges</c>.
    /// </summary>
    public long IssueId { get; set; }
    public EfHatchIssue? Issue { get; set; }

    [MaxLength(Common.PersonName.MaxChars)]
    public required string Author { get; set; }

    public required string Body { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One thing that happened to an issue. Append-only, written by every mutating
/// endpoint, never edited and never deleted except with its issue.
///
/// There is no reporting in the MVP and this table is still written from Phase
/// 1, because an event log is the one feature that cannot be added
/// retroactively: turned on in March, it answers nothing about February. The
/// cost is a row per edit and the benefit is that throughput, cycle time, and
/// "when did this actually move" are a query away rather than a migration away.
/// </summary>
/// <remarks>
/// Rank-only moves are deliberately not events. Dragging a card up its column
/// is board hygiene, not work, and logging it would bury the status changes
/// that matter under a hundred lines of tidying.
/// </remarks>
[Table("IssueEvents")]
[Index(nameof(IssueId), nameof(At))]
public class EfHatchIssueEvent
{
    public const int MaxKindLength = 32;

    /// <summary>The kinds, all of them. A string rather than an enum so a new one is a write, not a migration.</summary>
    public const string Created = "created";
    public const string Retitled = "retitled";
    public const string Redescribed = "redescribed";
    public const string Retyped = "retyped";
    public const string StatusChanged = "status_changed";
    public const string ParentChanged = "parent_changed";
    public const string ReadyChanged = "ready_changed";
    public const string DueChanged = "due_changed";
    public const string Commented = "commented";
    public const string Imported = "imported";

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>Set by EF fixup when the event is added through <see cref="EfHatchIssue.Events"/> - see <see cref="EfHatchComment.IssueId"/>.</summary>
    public long IssueId { get; set; }
    public EfHatchIssue? Issue { get; set; }

    [MaxLength(Common.PersonName.MaxChars)]
    public required string Actor { get; set; }

    [MaxLength(MaxKindLength)]
    public required string Kind { get; set; }

    /// <summary>
    /// What changed, as <c>jsonb</c>: <c>{ "from": ..., "to": ... }</c> for an
    /// edit, the source filename for an import. Schemaless on purpose - the
    /// shape differs per kind, and a column per field would be a migration
    /// every time a new verb is logged.
    /// </summary>
    public string? Payload { get; set; }

    public required DateTimeOffset At { get; set; }
}
