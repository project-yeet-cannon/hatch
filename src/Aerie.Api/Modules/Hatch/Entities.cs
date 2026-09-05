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
    /// The prefix of every issue key in this project. Changeable, behind a
    /// speed bump, and the cost of that is worth stating: every <c>AER-12</c>
    /// written into a commit message, a chat log, or a branch name goes dead,
    /// because those are references this database has never seen and cannot
    /// rewrite.
    /// </summary>
    /// <remarks>
    /// What does <em>not</em> break is the part that matters: parentage is a
    /// foreign key on <see cref="EfHatchIssue.ParentId"/>, and issue numbers are
    /// their own column, so a rekeyed project keeps every story under its epic
    /// and every task under its story - <c>AER-12</c> becomes <c>OPS-12</c>,
    /// same issue, same tree. Only text that spelled the old key out loud is
    /// left pointing at nothing.
    /// </remarks>
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
    public const int MaxColorLength = 7;

    /// <summary>What a column with nothing said about it wears: the neutral grey of a fact, not of a warning.</summary>
    public const string DefaultColor = "#6b7280";

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

    /// <summary>
    /// The column's colour, as <c>#rrggbb</c>. A row rather than a lookup in
    /// the frontend for the same reason the name is a row: the operator invents
    /// columns, and a palette keyed on the four names shipped here would leave
    /// "review" grey forever and would lose a column's colour the moment it was
    /// renamed.
    /// </summary>
    /// <remarks>
    /// Stored as a hex string rather than as a token name because the value has
    /// to survive a theme the operator has not chosen yet, and because a status
    /// picker that offers eight token names is a picker that says no to the
    /// ninth colour somebody wants.
    /// </remarks>
    [MaxLength(MaxColorLength)]
    public string Color { get; set; } = DefaultColor;

    private static readonly Regex ColorShape =
        new("^#[0-9a-f]{6}$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Whether a string is a colour this will store. Six digits and a hash,
    /// deliberately narrow: three-digit shorthands, <c>rgb()</c> and named
    /// colours would all have to be normalised somewhere before a stylesheet or
    /// a contrast calculation could read them, and one shape stored is one
    /// shape to reason about.
    /// </summary>
    public static bool IsValidColor(string? color) =>
        color is not null && ColorShape.IsMatch(color);

    /// <summary>The stored form of a colour a client sent: lower case, so two spellings of one colour compare equal.</summary>
    public static string NormalizeColor(string color) => color.Trim().ToLowerInvariant();
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

/// <summary>
/// A comment on an issue. Markdown, like the description, and rendered the same
/// way.
///
/// Most comments are notes - a commit sha, a summary for a reviewer, a change
/// of mind. Two of them are not, and those two carry a <see cref="Kind"/>: a
/// <see cref="Question"/> is an agent saying it cannot proceed without a
/// decision only the operator can make, and an <see cref="Answer"/> is that
/// decision, bound to the question it settles by <see cref="AnswersId"/>.
/// </summary>
/// <remarks>
/// A question is a row rather than a heading in a comment body for the same
/// reason a ready date is a column rather than a line saying "not until March":
/// something has to be able to act on it. An unanswered question is why a
/// ticket cannot move, so <see cref="WorkController"/> reads these to refuse a
/// run and the board reads them to say which cards are waiting on a person.
/// Prose in a thread can be searched; it cannot be counted.
/// </remarks>
[Table("Comments")]
[Index(nameof(IssueId), nameof(CreatedAt))]
[Index(nameof(AnswersId))]
public class EfHatchComment
{
    public const int MaxBodyLength = 100_000;
    public const int MaxKindLength = 16;

    /// <summary>An ordinary comment. The empty string rather than null, so the column never has two ways to say "nothing special".</summary>
    public const string Note = "";

    /// <summary>A decision being asked for. Open until some comment answers it.</summary>
    public const string Question = "question";

    /// <summary>A decision being given, pointing at the question it settles.</summary>
    public const string Answer = "answer";

    public static bool IsValidKind(string kind) => kind is Note or Question or Answer;

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

    /// <summary>
    /// <see cref="Note"/>, <see cref="Question"/> or <see cref="Answer"/>.
    /// Defaulted rather than required: every comment written before this column
    /// existed is a note, and so is every comment written by a client that does
    /// not know about the other two.
    /// </summary>
    [MaxLength(MaxKindLength)]
    public string Kind { get; set; } = Note;

    /// <summary>
    /// The question this answers, on the same issue. Null on everything else.
    /// </summary>
    /// <remarks>
    /// The link points from the answer to the question and not the other way
    /// round, which is what lets a question be answered twice without an edit -
    /// somebody refining a decision writes a second answer, and the first stays
    /// where it was said. "Open" is therefore a question with no answers
    /// pointing at it, computed and never stored, so the two can never disagree.
    /// </remarks>
    public long? AnswersId { get; set; }
    public EfHatchComment? Answers { get; set; }

    /// <summary>The answers to this question, if it is one.</summary>
    public ICollection<EfHatchComment> AnsweredBy { get; set; } = [];

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

    /// <summary>A question was asked, and the issue is waiting on a person until it is answered.</summary>
    public const string Asked = "asked";

    /// <summary>A question was answered. The payload names which one.</summary>
    public const string Answered = "answered";

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

/// <summary>
/// What an agent should be told, and how much thought to spend, when it moves
/// an issue of some type from one column to the next.
///
/// The matrix exists because "do the next increment of work" is not one job.
/// Turning a paragraph of intent into an epic with stories under it is the
/// hardest thinking in the flow and wants the largest model at the highest
/// effort; picking up an already-specified task and writing the code is
/// ordinary work that a smaller one does well. Encoding that as rows rather
/// than as branches in a script means the operator retunes it from a page
/// after watching a run go badly, which is the only way anybody ever finds the
/// right settings.
/// </summary>
/// <remarks>
/// Deliberately not writable by an API key - see
/// <see cref="PlaybooksController"/>. A playbook chooses the model and the
/// prompt for the next agent, so an agent that could edit one could widen its
/// own instructions and its own budget, and the loop that results has no
/// natural end. The operator writes these; agents read them.
/// </remarks>
[Table("Playbooks")]
[Index(nameof(FromStatusId), nameof(ToStatusId), nameof(Types), IsUnique = true)]
public class EfHatchPlaybook
{
    public const int MaxTypesLength = 60;
    public const int MaxModelLength = 60;
    public const int MaxEffortLength = 10;
    public const int MaxPromptLength = 20_000;

    /// <summary>
    /// The thinking budget, as the Claude Code CLI spells it - what
    /// <c>--effort</c> accepts and nothing else, because a value this does not
    /// recognise is one the CLI refuses at spawn time, long after the operator
    /// has stopped looking at the page they typed it on.
    /// </summary>
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The model aliases the CLI resolves to whatever is current. Aliases
    /// rather than pinned ids because a playbook says "the big one" and means
    /// it a year from now; a full <c>claude-…</c> name is accepted too, for an
    /// operator who has a reason to pin.
    /// </summary>
    public static readonly string[] ModelAliases = ["haiku", "sonnet", "opus", "fable"];

    /// <summary>A pinned model id, for the operator who wants exactly one.</summary>
    private const string FullModelPattern = "^claude-[a-z0-9][a-z0-9.-]{0,48}$";

    /// <summary>
    /// What a row created without an opinion takes. The middle of the range on
    /// both axes, deliberately: a playbook nobody has tuned yet should do the
    /// work adequately and cost adequately, so that the first thing the
    /// operator learns from it is what the transition actually needs.
    /// </summary>
    public const string DefaultModel = "sonnet";
    public const string DefaultEffort = "medium";

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>The column the issue is sitting in when the agent picks it up.</summary>
    public required int FromStatusId { get; set; }
    public EfHatchStatus? FromStatus { get; set; }

    /// <summary>The column it is meant to be in when the agent stops.</summary>
    public required int ToStatusId { get; set; }
    public EfHatchStatus? ToStatus { get; set; }

    /// <summary>
    /// Which issue types this applies to, comma separated and normalised by
    /// <see cref="NormalizeTypes"/> - or empty, which means every type.
    ///
    /// A string rather than a join table because the set has four members and
    /// is read whole every time it is read at all; the unique index above is
    /// what the normalisation is for, and it is why "task,bug" and "bug, task"
    /// must not be two rows.
    /// </summary>
    [MaxLength(MaxTypesLength)]
    public required string Types { get; set; }

    /// <summary>
    /// What the agent is told before it is shown the ticket. The ticket body is
    /// the brief; this is the method - what "one increment" means for this
    /// transition, and what shape the answer takes.
    /// </summary>
    [MaxLength(MaxPromptLength)]
    public required string Prompt { get; set; }

    [MaxLength(MaxModelLength)]
    public required string Model { get; set; }

    [MaxLength(MaxEffortLength)]
    public required string Effort { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Lower case, de-duplicated, in the order <see cref="EfHatchIssue.Types"/>
    /// declares them, joined with commas. One set of types has exactly one
    /// spelling, so the unique index can do its job.
    /// </summary>
    public static string NormalizeTypes(IEnumerable<string>? types)
    {
        if (types is null) return "";

        var wanted = types
            .Select(t => t?.Trim().ToLowerInvariant())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToHashSet();

        // Every type named is the same as none named - both mean "any issue" -
        // and storing it as the empty set keeps one meaning to one row.
        if (wanted.Count == 0 || EfHatchIssue.Types.All(wanted.Contains)) return "";

        return string.Join(",", EfHatchIssue.Types.Where(wanted.Contains));
    }

    /// <summary>The stored string back as a list. Empty stays empty.</summary>
    public static string[] SplitTypes(string? types) =>
        string.IsNullOrEmpty(types) ? [] : types.Split(',', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether this playbook speaks for an issue of this type.</summary>
    public bool Covers(string issueType) =>
        Types.Length == 0 || SplitTypes(Types).Contains(issueType);

    /// <summary>
    /// How closely it speaks for it. A playbook that names the type beats one
    /// that names every type, so "inbox to todo, epics" can say something
    /// different from "inbox to todo, anything else" without either having to
    /// know about the other.
    /// </summary>
    public int Specificity => Types.Length == 0 ? 0 : 1;

    public static bool IsValidEffort(string? effort) =>
        effort is not null && Efforts.Contains(effort);

    public static bool IsValidModel(string? model) =>
        model is not null &&
        (ModelAliases.Contains(model) ||
         Regex.IsMatch(model, FullModelPattern, RegexOptions.None, TimeSpan.FromSeconds(1)));
}
