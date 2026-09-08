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
/// It is deliberately not a container. The board (docs/hatch.md, "Goals")
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
    /// path catches and retries (docs/hatch.md, "Issue numbering"). The
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

    /// <summary>
    /// Room for a pull request URL. Generous against the forge URLs anybody
    /// actually pastes - a long branch name on a long repository path is a
    /// couple of hundred characters - and short enough that the column is not
    /// somewhere a description ends up by mistake.
    /// </summary>
    public const int MaxPullRequestUrlLength = 500;

    /// <summary>
    /// Room for <c>host:/path/to/checkout</c>, which is how a runner names
    /// itself. The same 240 a person's name takes, because it is the same kind
    /// of thing: a label somebody reads in a refusal.
    /// </summary>
    public const int MaxClaimRunnerLength = ClaimRequest.MaxRunnerLength;

    /// <summary>
    /// Room for a line of chatter. A sentence, not a log - what is stored is
    /// what a card draws under "working on it", and anything longer is
    /// truncated to this.
    /// </summary>
    public const int MaxClaimChatterLength = ClaimRequest.MaxChatterLength;

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
    /// Where the work is being reviewed: an absolute http(s) URL, or null until
    /// something opens one. Held here so that "show me the pull request" is a
    /// link on the issue rather than a search through the comments for a URL
    /// somebody remembered to paste.
    /// </summary>
    /// <remarks>
    /// One URL rather than a list, deliberately. The field answers "where is
    /// this being reviewed <em>now</em>", and the question "what has it been"
    /// is already answered by the event log, which keeps every value this
    /// column has ever held. A list would be a second history beside that one,
    /// and worse than it.
    ///
    /// Not a foreign key to anything and not parsed for a host: Aerie has no
    /// opinion about whose forge an operator uses, and a column that only
    /// accepted one would be a fact about exactly one installation
    /// (docs/ethos.md).
    /// </remarks>
    [MaxLength(MaxPullRequestUrlLength)]
    public string? PullRequestUrl { get; set; }

    /// <summary>
    /// The model every agent increment dispatched for this issue runs on,
    /// whichever playbook speaks for the move - or null, which is every issue
    /// on a stock board and means "whatever the playbook says".
    /// </summary>
    /// <remarks>
    /// A playbook prices a transition, and that is the right unit almost
    /// always. What it cannot say is that <em>this</em> story is the hard one:
    /// the knob for that used to be a flag on one command at a terminal, which
    /// is no use at three in the morning when the loop is the only thing
    /// typing. So the fact lives on the ticket somebody looked at.
    ///
    /// It reaches this issue and nothing beneath it. An epic set to
    /// <c>opus</c> does not spend <c>opus</c> on its stories - a task that
    /// needs the big model says so itself, and the alternative is an
    /// expensive decision made once at the top of a tree and inherited by
    /// work nobody weighed.
    ///
    /// Sized by the playbook's own constants because it holds the playbook's
    /// own values, and validated by
    /// <see cref="EfHatchPlaybook.IsValidModel"/> - one definition of a legal
    /// model, so what an issue may be set to and what a playbook may be set
    /// to cannot drift apart. Writing it is closed to an API key
    /// (<see cref="IssuePlaybookController"/>) for the same reason writing a
    /// playbook is.
    /// </remarks>
    [MaxLength(EfHatchPlaybook.MaxModelLength)]
    public string? ModelOverride { get; set; }

    /// <summary>
    /// The thinking budget those increments run at, or null for the
    /// playbook's - independent of <see cref="ModelOverride"/> in both
    /// directions, because "this one is subtle" and "this one is large" are
    /// different complaints about a ticket.
    /// </summary>
    [MaxLength(EfHatchPlaybook.MaxEffortLength)]
    public string? EffortOverride { get; set; }

    // ---- The claim ----
    //
    // A lease on this issue held by a running dispatcher: taken before an
    // increment, refreshed while it runs, released after it, and expiring on
    // its own when the runner dies. Seven columns on the issue row rather than
    // a table of their own, because a claim is a fact about the issue with at
    // most one of it at a time - and because taking one is then a single
    // conditional UPDATE against a row the dispatcher is already reading.
    //
    // Expiry is lazy and there is no sweeper. A claim is dead when
    // <see cref="ClaimHeartbeatAt"/> is older than the TTL, which is a
    // predicate every reader evaluates (see IssueClaims.IsLive); nothing has to
    // run for a dead runner's ticket to become claimable again. The columns are
    // left as they are until somebody takes the lease over, so the trail of who
    // last held it survives the lease itself.
    //
    // There is deliberately no index on ClaimHeartbeatAt, and nobody should add
    // one on a hunch. The predicate is only ever evaluated against rows already
    // selected by the board's own (StatusId, Rank) index or by primary key, and
    // this table holds hundreds of rows.

    /// <summary>
    /// The fencing token, or null where nothing holds this issue. A heartbeat
    /// or a release presenting a token that is not this one is refused, which
    /// is what keeps a runner whose lease expired mid-increment from clearing
    /// the lease that replaced it.
    /// </summary>
    /// <remarks>
    /// Never leaves the server on a board read - see <see cref="IssueClaimDto"/>.
    /// It is a capability, not a fact about the issue, and a card carrying it
    /// would let anyone holding a board read steal or refresh a lease.
    /// </remarks>
    public Guid? ClaimToken { get; set; }

    /// <summary>
    /// Who holds it, as a name rather than a foreign key - the same column
    /// shape <see cref="CreatedBy"/> and <see cref="EfHatchIssueEvent.Actor"/>
    /// take, and for the same reason: the trail has to keep reading after the
    /// person or the key it named is gone.
    /// </summary>
    [MaxLength(Common.PersonName.MaxChars)]
    public string? ClaimedBy { get; set; }

    /// <summary>
    /// Which checkout is holding it - <c>host:/path/to/checkout</c>, as the
    /// runner names itself. A claim answers "who" and "from where" separately
    /// because two checkouts on one box under one key are the case the whole
    /// feature exists for.
    /// </summary>
    [MaxLength(MaxClaimRunnerLength)]
    public string? ClaimRunner { get; set; }

    /// <summary>When the lease was taken. Not moved by a heartbeat - "how long has this been running" is a question about this column.</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>When the holder was last heard from. The lease is over when this is older than the TTL.</summary>
    public DateTimeOffset? ClaimHeartbeatAt { get; set; }

    /// <summary>
    /// A line the holder is carrying: what it is doing right now, replaced
    /// whole by each heartbeat that names one. Cosmetic, and truncated rather
    /// than refused - killing a live lease because a terminal printed something
    /// wide would be the wrong trade.
    /// </summary>
    [MaxLength(MaxClaimChatterLength)]
    public string? ClaimChatter { get; set; }

    /// <summary>When that line arrived, so a stale one reads as stale.</summary>
    public DateTimeOffset? ClaimChatterAt { get; set; }

    /// <summary>
    /// The person this issue belongs to, or null. At most one of this and
    /// <see cref="AssigneeApiKeyId"/> is ever set - the controller clears the
    /// other on every write, and a check constraint refuses a row wearing both.
    /// </summary>
    /// <remarks>
    /// <para>A bare <see cref="Guid"/> and not a foreign key, for the reason
    /// Quill's <c>QuillNote.PersonId</c> is one and
    /// <see cref="CreatedBy"/> is a name: Hatch owns its own schema and its own
    /// migration history, and a constraint from a module into
    /// <c>public.People</c> is the compile-time coupling Modules/README.md
    /// exists to prevent.</para>
    ///
    /// <para>What <c>ON DELETE SET NULL</c> would have bought is bought instead
    /// by a predicate every reader applies: an id that does not resolve to a
    /// live identity reads as unassigned, in the DTO, on the card, in the filter
    /// and at the dispatcher, at the same instant and with nothing to sweep. See
    /// <see cref="Services.Auth.IActorDirectory"/>, which owns that rule.</para>
    ///
    /// <para>Deliberately not a claim. A claim is a machine lease that comes and
    /// goes with an increment; this is a durable statement about who owns the
    /// ticket, written by a person and surviving every restart - which is why
    /// writing it is closed to an API key
    /// (<see cref="AssigneeController"/>).</para>
    /// </remarks>
    public Guid? AssigneePersonId { get; set; }

    /// <summary>
    /// The API key this issue belongs to, or null - the same column for the
    /// other kind of actor, and mutually exclusive with
    /// <see cref="AssigneePersonId"/>.
    ///
    /// A revoked key still has a row (<see cref="Ef.EfApiKey.RevokedAt"/>), so this
    /// pointing at one is not an error and is not cleaned up: it reads as
    /// unassigned, and the <c>assignee_changed</c> event still names who it was.
    /// </summary>
    public Guid? AssigneeApiKeyId { get; set; }

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
    public ICollection<EfHatchWorkLogEntry> WorkLog { get; set; } = [];

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

    /// <summary>
    /// The answers a question offers, as <c>jsonb</c>: a small ordered list of
    /// <see cref="QuestionOptionDto"/>. Null on a question asked in prose, and
    /// on everything that is not a question.
    /// </summary>
    /// <remarks>
    /// A column rather than a convention in the body, for the reason the kind
    /// is a column: something has to be able to act on it. An agent asking
    /// "leaf-weighted or child-weighted?" is not writing an essay, it is
    /// offering a choice - and a choice the reader can press is a decision made
    /// in one gesture instead of a paragraph parsed by eye. Prose in a body can
    /// be read; it cannot be clicked.
    ///
    /// <c>jsonb</c> and not a table of its own, on the same grounds as
    /// <see cref="EfHatchIssueEvent.Payload"/>: this is a closed list read whole
    /// with the row that owns it and never queried across, so a table would buy
    /// a join and a cascade in exchange for nothing.
    ///
    /// Which option was taken is deliberately not stored. An answer's body is
    /// the option's label, which is the sentence a person reads six months
    /// later and the sentence the next agent's prompt carries - and a second
    /// column saying the same thing in numbers is a second thing that can come
    /// to disagree with the first.
    /// </remarks>
    public string? Options { get; set; }

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

    /// <summary>
    /// The issue was pointed at a pull request, or taken off one. The field
    /// holds one URL; this is what makes the trail hold every URL it has ever
    /// held - see <see cref="EfHatchIssue.PullRequestUrl"/>.
    /// </summary>
    public const string PullRequestChanged = "pull_request_changed";

    /// <summary>
    /// The issue was given to somebody, handed to somebody else, or taken off
    /// everybody. The payload's <c>from</c> and <c>to</c> each carry a
    /// <c>name</c> beside the kind and the id, and that is load-bearing: an
    /// assignee resolves only while the identity is live, so an id alone would
    /// stop reading the day a person is deleted or a key is revoked - which is
    /// exactly when "whose was this in March" gets asked. The same argument
    /// <see cref="EfHatchIssue.CreatedBy"/> makes for being a name.
    /// </summary>
    public const string AssigneeChanged = "assignee_changed";

    /// <summary>
    /// The issue was pinned to a model, moved to another, or handed back to
    /// the playbook. What a ticket was spent on is a question somebody asks
    /// after the bill, so the trail holds every value the column has held -
    /// see <see cref="EfHatchIssue.ModelOverride"/>.
    /// </summary>
    public const string ModelOverrideChanged = "model_override_changed";

    /// <summary>The same, for the thinking budget - see <see cref="EfHatchIssue.EffortOverride"/>.</summary>
    public const string EffortOverrideChanged = "effort_override_changed";

    /// <summary>
    /// The issue was made to wait on another, or freed from one. Written on the
    /// issue that waits and on it alone - the edge is that issue's, and a second
    /// event on the blocker would be the same fact filed twice.
    /// </summary>
    public const string DependencyAdded = "dependency_added";

    /// <summary>The reverse - see <see cref="DependencyAdded"/>.</summary>
    public const string DependencyRemoved = "dependency_removed";

    /// <summary>
    /// A runner took the lease on this issue - see
    /// <see cref="EfHatchIssue.ClaimToken"/>. Written because the trail's
    /// question is "who took this ticket", and on a board worked by several
    /// checkouts that is a question with a wrong answer.
    /// </summary>
    public const string ClaimTaken = "claim_taken";

    /// <summary>The holder let go of it, presenting the token it was given.</summary>
    public const string ClaimReleased = "claim_released";

    /// <summary>
    /// The operator took it off somebody - the same column cleared, and a
    /// different fact. A runner letting go and a person prising a ticket loose
    /// are told apart by the kind and not by the payload, which is the same
    /// <c>{ from, to }</c> shape either way.
    /// </summary>
    public const string ClaimCleared = "claim_cleared";

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

/// <summary>
/// One issue waits on another: an edge saying that <see cref="DependsOn"/> must
/// be done before <see cref="Issue"/> is implemented.
///
/// The dependency, and not shared parentage, is what serialises work. An epic
/// whose five stories must land one after another gets a chain of these and the
/// loop walks it in order; an epic whose five stories are independent gets none
/// and they go in whatever order the board puts them in. The rule this replaced
/// - no sibling awaiting review - was a guess about every parent on the board,
/// true of the epics whose stories touch the same files and wrong about the
/// rest.
/// </summary>
/// <remarks>
/// A row with an <c>Id</c> and an author rather than a bare join table, for the
/// reason every other table in the module has one: who said two things must
/// land in order, and when, is worth keeping, and a join entity that already
/// exists is where the next field goes.
///
/// The unique index is what makes re-adding an edge a no-op rather than a
/// second row - a property of the table instead of a check somebody remembered.
/// The index on <see cref="DependsOnId"/> alone is the reverse direction, which
/// the <c>Blocks</c> list and the dispatcher's gate both read, and is there for
/// the reason <see cref="EfHatchComment.AnswersId"/> carries one.
/// </remarks>
[Table("IssueDependencies")]
[Index(nameof(IssueId), nameof(DependsOnId), IsUnique = true)]
[Index(nameof(DependsOnId))]
public class EfHatchIssueDependency
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>The issue that waits.</summary>
    public required long IssueId { get; set; }
    public EfHatchIssue? Issue { get; set; }

    /// <summary>The issue waited on. Satisfied only once it sits in a terminal column.</summary>
    public required long DependsOnId { get; set; }
    public EfHatchIssue? DependsOn { get; set; }

    [MaxLength(Common.PersonName.MaxChars)]
    public required string CreatedBy { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// What one agent session cost, on the ticket it was spent on.
///
/// A row per unattended increment: the session id <c>claude --resume</c> takes,
/// how long it ran, what it burned in tokens and in notional dollars, and a
/// sentence saying what it did. Account-wide utilization is honest but
/// anonymous - no arithmetic over it can say which epic ate the evening - and
/// the dispatcher is the only thing in the system that knows which ticket a
/// session's spend was for. This table is that knowledge, written down.
///
/// The dollars are notional API list price as the CLI reports it, not money
/// that left an account: on a subscription nothing was billed per session. Both
/// figures are stored and both go out on the wire, so which one is the headline
/// is a decision the page makes and not one the schema has baked in.
/// </summary>
/// <remarks>
/// No author column, and it is the one table in the module without one. Every
/// other row records who acted; here the actor <em>is</em> the session, whose
/// id is already the row's identity.
///
/// Two limits are accepted rather than designed around. An interactive session
/// - <c>hatch.sh work -i</c>, or one resumed by hand - leaves no row, because
/// nothing is watching its exit; that hole is smaller than making every
/// terminal session a writer. And a run that dies before it can describe itself
/// still gets a row, marked undescribed: losing an evening's spend because a
/// session could not compose a summary would be the wrong trade.
///
/// Nothing prunes these. One row per increment is small, and the day it is not,
/// a retention window is a sampler's worth of work rather than a schema change.
/// </remarks>
[Table("WorkLogEntries")]
// What makes a retried write idempotent rather than a second row - the same
// property EfHatchIssueDependency's unique index has, and for the same reason:
// the caller is a shell script at the end of a run, and a run can be re-run.
[Index(nameof(IssueId), nameof(SessionId), IsUnique = true)]
// The issue page's one query: this issue's entries, newest first.
[Index(nameof(IssueId), nameof(EndedAt))]
public class EfHatchWorkLogEntry
{
    public const int MaxSessionIdLength = 64;
    public const int MaxTitleLength = 200;
    public const int MaxSummaryLength = 2000;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary>Set by EF fixup when the entry is added through <see cref="EfHatchIssue.WorkLog"/> - see <see cref="EfHatchComment.IssueId"/>.</summary>
    public long IssueId { get; set; }
    public EfHatchIssue? Issue { get; set; }

    /// <summary>What <c>claude --resume</c> takes, and this row's identity within its issue.</summary>
    [MaxLength(MaxSessionIdLength)]
    public required string SessionId { get; set; }

    /// <summary>Wall clock either side of the CLI, taken by the dispatcher.</summary>
    /// <remarks>
    /// Deliberately not reconciled with <see cref="DurationMs"/>, which is what
    /// the session itself reported. The pair says when the increment occupied
    /// the machine; the duration says how long it was thinking, which is the
    /// smaller number and the honest answer to "how long did this take".
    /// </remarks>
    public required DateTimeOffset StartedAt { get; set; }
    public required DateTimeOffset EndedAt { get; set; }

    /// <summary>The session's own <c>duration_ms</c> - see <see cref="StartedAt"/>.</summary>
    public required long DurationMs { get; set; }

    /// <summary>What the session did, in a few words. Null when it never said.</summary>
    [MaxLength(MaxTitleLength)]
    public string? Title { get; set; }

    /// <summary>The same, at length. Sessions are asked to keep it under 100 words.</summary>
    [MaxLength(MaxSummaryLength)]
    public string? Summary { get; set; }

    /// <summary>
    /// Whether the session said what it did. Set by the server from
    /// <see cref="Title"/> and <see cref="Summary"/> and never sent by a caller,
    /// so the two cannot come to disagree.
    /// </summary>
    /// <remarks>
    /// A column rather than a client-side test for an empty title, because
    /// "this run never described itself" is a fact about the run and a client
    /// inferring it from an absent string is a client guessing.
    /// </remarks>
    public bool Described { get; set; }

    /// <summary>The session's <c>is_error</c>. Its spend counted either way.</summary>
    public bool IsError { get; set; }

    /// <summary>The session's <c>num_turns</c>.</summary>
    public int Turns { get; set; }

    /// <summary>
    /// Notional API list price, in dollars, as the CLI reported it. Eight
    /// places because a short session costs a fraction of a cent and rounding
    /// it to two would make a night of them add up to nothing.
    /// </summary>
    public decimal CostUsd { get; set; }

    /// <summary>
    /// The session's four token counts, summed over <see cref="ModelUsage"/>.
    /// </summary>
    /// <remarks>
    /// A denormalisation, said out loud because a denormalisation nobody wrote
    /// down is a bug waiting for its second writer: these are the sum of the
    /// breakdown and are computed from it by the endpoint that writes the row,
    /// never sent independently. They are columns and not a fold over the json
    /// because every total in the hierarchy is an aggregate over them.
    /// </remarks>
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public long CacheReadTokens { get; set; }

    /// <summary>
    /// The per-model breakdown the four counts are the sum of, as <c>jsonb</c>:
    /// an ordered list of <see cref="WorkLogModelUseDto"/>. Null when the run
    /// ended before the accounting arrived.
    /// </summary>
    /// <remarks>
    /// <c>jsonb</c> and not a table, on the grounds
    /// <see cref="EfHatchIssueEvent.Payload"/> is: it is read whole with the row
    /// that owns it, and a table would buy a join and a cascade for nothing. The
    /// day somebody asks which model a quarter went on, it is a query against
    /// this column rather than a migration.
    /// </remarks>
    public string? ModelUsage { get; set; }

    /// <summary>When the row landed, which is not when the session ended.</summary>
    public required DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A runner: one <c>go-to-work</c> or <c>work</c> process, and what the board
/// would like it to do next.
///
/// The row is written from two directions and they never overlap. The process
/// writes <see cref="Kind"/>, <see cref="LastSeenAt"/> and <see cref="Line"/>
/// on every heartbeat - "I am here, and this is what I am doing". The operator
/// writes <see cref="State"/> and the four bounds - "keep going", "finish and
/// stop", "stay under this epic" - and a heartbeat never overwrites one of
/// those once the row exists. What the loop reads back off its own heartbeat is
/// the second half, which is the whole round trip: nothing on the server starts
/// or stops a process, it says what it would like and the runner obeys between
/// increments.
/// </summary>
/// <remarks>
/// <para>There is no claim key here, deliberately, and it is the one thing
/// somebody adding a column to this table is most likely to want. What a runner
/// is working is <see cref="EfHatchIssue.ClaimRunner"/> read back the other way
/// - <c>Issues WHERE ClaimRunner = Name</c>, judged live - the same way an open
/// question is computed rather than stored. A column here would be a copy that
/// drifts the moment an operator clears a claim out from under the runner
/// holding it.</para>
///
/// <para>Nothing sweeps this table. A row ages through idle, then gone, then
/// out of the read entirely - all three by arithmetic against
/// <see cref="LastSeenAt"/> at the moment somebody asks, which is the same lazy
/// expiry the claim uses and for the same reason: there is no timeout to tune
/// and no sweeper to notice has stopped.</para>
/// </remarks>
[Table("Runners")]
public class EfHatchRunner
{
    /// <summary>Room for <c>host:/path/to/checkout</c> - the same name a claim carries, because it is the same name.</summary>
    public const int MaxNameLength = ClaimRequest.MaxRunnerLength;

    /// <summary>Room for a line the runner printed, on the terms a claim's chatter has.</summary>
    public const int MaxLineLength = ClaimRequest.MaxChatterLength;

    public const int MaxKindLength = 16;
    public const int MaxStateLength = 16;

    /// <summary>Room for an issue key, which is what <c>--under</c> takes.</summary>
    public const int MaxUnderLength = 32;

    // The five words both sides of the wire act on, read off the contract
    // rather than written down again here - the runner sends one of the kinds
    // and obeys one of the states, and the copy that would be wrong is always
    // the one nobody was looking at.
    public const string LoopKind = RunnerKinds.Loop;
    public const string OnceKind = RunnerKinds.Once;
    public const string Running = RunnerStates.Running;
    public const string Paused = RunnerStates.Paused;
    public const string Stopping = RunnerStates.Stopping;

    /// <summary>The three a <c>PATCH</c> accepts, in the order a control surface should offer them.</summary>
    public static readonly string[] States = RunnerStates.All;

    /// <summary>
    /// What the runner calls itself - <c>host:/path/to/checkout</c>, or
    /// <c>HATCH_RUNNER</c>'s override. The key, because a runner already has
    /// exactly one identity and it is this one; a surrogate id would be a
    /// second name to keep in step with the claim's.
    /// </summary>
    [Key]
    [MaxLength(MaxNameLength)]
    public required string Name { get; set; }

    /// <summary>
    /// <see cref="LoopKind"/> or <see cref="OnceKind"/> - whether anything will
    /// ever read an instruction back. Written by every heartbeat rather than
    /// only on insert: the same checkout runs both commands, and a row that
    /// still said <c>loop</c> would offer a control surface for a process that
    /// exited an hour ago.
    /// </summary>
    [MaxLength(MaxKindLength)]
    public required string Kind { get; set; }

    /// <summary>The first time this name was ever seen, and never moved afterwards.</summary>
    public required DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>
    /// The last heartbeat. Every judgement about this row - idle, gone, dropped
    /// - is arithmetic against this and nothing else.
    /// </summary>
    public required DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// The last line the runner printed while holding no claim: what it is
    /// waiting for, why it is idle, why it stopped. In-increment chatter rides
    /// the claim instead and is read from there, so this is only ever drawn for
    /// a runner between tickets.
    /// </summary>
    [MaxLength(MaxLineLength)]
    public string? Line { get; set; }

    /// <summary>When that line arrived, so a stale one reads as stale.</summary>
    public DateTimeOffset? LineAt { get; set; }

    /// <summary>
    /// What the board would like this runner to do: <see cref="Running"/>,
    /// <see cref="Paused"/> or <see cref="Stopping"/>. Written by a person and
    /// never by a heartbeat - an agent that could set its own state could
    /// un-stop itself.
    /// </summary>
    [MaxLength(MaxStateLength)]
    public required string State { get; set; }

    /// <summary>
    /// The epic to stay inside, or null for the whole board -
    /// <c>go-to-work --under</c>, moved to the board.
    /// </summary>
    [MaxLength(MaxUnderLength)]
    public string? Under { get; set; }

    /// <summary>
    /// The cap on increments, compared against a count the runner keeps itself.
    /// Lowering it below what a night has already spent stops that loop at its
    /// next pass, which is the honest reading of a cap.
    /// </summary>
    public int? MaxRuns { get; set; }

    /// <summary>The cap on dollars, on the same terms.</summary>
    public decimal? MaxSpend { get; set; }

    /// <summary>
    /// When to stop, always as an instant. The command line takes a wall clock
    /// (<c>--until 06:00</c>) because somebody is typing it at a terminal in a
    /// timezone; a row on a page is read by a browser that knows its own, so
    /// the stored form is the unambiguous one.
    /// </summary>
    public DateTimeOffset? UntilAt { get; set; }
}
