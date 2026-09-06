using System.Text.Json;

namespace Aerie.Api.Modules.Hatch;

// ---- Projects ----

/// <param name="IssueCount">
/// What the delete guard will look at, so the Projects page can grey the button
/// rather than offer a 409.
/// </param>
public record ProjectDto(int Id, string Key, string Name, int IssueCount, DateTimeOffset CreatedAt);

/// <summary>
/// A new project. <paramref name="Key"/> is checked against
/// <see cref="EfHatchProject.KeyPattern"/> and against every existing key. It
/// can be changed later, at a price the operator is shown first - see
/// <see cref="EfHatchProject.Key"/>.
/// </summary>
public record ProjectCreateRequest(string Key, string Name);

/// <summary>
/// A rename. <paramref name="Name"/> is what usually moves;
/// <paramref name="Key"/> is the one that costs something and is null in almost
/// every request - see <see cref="EfHatchProject.Key"/> for what a rekey breaks
/// and what it does not.
/// </summary>
public record ProjectPatchRequest(string? Name, string? Key = null);

// ---- Statuses ----

/// <param name="Color">
/// <c>#rrggbb</c>, lower case. Carried on every column the board draws, because
/// the colour is what makes a column identifiable at a glance once the cards in
/// it are too narrow to read.
/// </param>
public record StatusDto(int Id, string Name, int SortOrder, bool IsTerminal, string Color);

/// <summary>
/// A new column. The optional fields each have a server-side default -
/// rightmost position, not terminal, and <see cref="EfHatchStatus.DefaultColor"/>
/// - so the shortest way to add a column is still a name.
/// </summary>
public record StatusCreateRequest(string Name, int? SortOrder, bool? IsTerminal, string? Color = null);

/// <summary>Every field optional: null means "leave this one alone".</summary>
public record StatusPatchRequest(string? Name, int? SortOrder, bool? IsTerminal, string? Color = null);

// ---- Issues ----

/// <summary>
/// A card. What the board draws, and nothing more - descriptions and comments
/// are a detail-page request, because the board holds every issue in the house
/// and shipping every description with it would make the first paint the
/// slowest one.
/// </summary>
/// <param name="Key">The display key, <c>AER-12</c>. Computed from the project and number, never stored.</param>
/// <param name="ReadyAt">
/// Carried on the card because the board decides what to fold away with it -
/// see <see cref="BoardDto"/>.
/// </param>
/// <param name="OpenQuestions">
/// How many questions on this issue nobody has answered. A count rather than
/// the questions themselves: the board draws a badge and the detail page is
/// where they are read. Defaulted, because the lists that are not the board -
/// an issue's children, a search result - are not places anybody answers a
/// question from, and counting for them would be a query nobody reads.
/// </param>
public record IssueCardDto(
    string Key,
    string ProjectKey,
    string Type,
    string Title,
    int StatusId,
    long Rank,
    string? ParentKey,
    string? ReadyAt,
    string? DueAt,
    int OpenQuestions = 0);

/// <summary>One issue, whole - the detail page's payload.</summary>
/// <param name="ChildKeys">Its stories, or its tasks. Keys rather than nested issues: the page links to them and does not draw them.</param>
/// <param name="ReadyAt">
/// When the issue becomes workable, or null if it always was. A bare date
/// (<c>2026-09-12</c>) or an instant (<c>2026-09-12T17:00:00Z</c>) - the two
/// forms mean different things and <see cref="IssueMoment"/> says how.
/// </param>
/// <param name="DueAt">When it is owed, in the same two forms, or null.</param>
/// <param name="PullRequestUrl">
/// Where the work is being reviewed, or null while it is nowhere. An absolute
/// http(s) URL - see <see cref="EfHatchIssue.PullRequestUrl"/> for why it is one
/// and not a list of them.
/// </param>
public record IssueDto(
    string Key,
    int ProjectId,
    string ProjectKey,
    string Type,
    string Title,
    string Description,
    int StatusId,
    long Rank,
    string? ParentKey,
    IReadOnlyList<string> ChildKeys,
    string? ReadyAt,
    string? DueAt,
    string? PullRequestUrl,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A new issue. It lands in the leftmost status and at the bottom of that
/// column - the server decides both, so no client has to know what "the inbox"
/// is called this week.
/// </summary>
public record IssueCreateRequest(
    int ProjectId,
    string Type,
    string Title,
    string? Description,
    string? ParentKey,
    string? ReadyAt,
    string? DueAt);

/// <summary>
/// An edit. Every field is optional and null means "leave this alone", which
/// leaves one thing that needs saying out loud: an empty
/// <paramref name="ParentKey"/> - <c>""</c> - clears the parent. A JSON body
/// cannot otherwise distinguish "no opinion" from "no parent", and the empty
/// string is unambiguous because no issue key can ever be one.
///
/// <paramref name="ReadyAt"/>, <paramref name="DueAt"/> and
/// <paramref name="PullRequestUrl"/> read the empty string the same way, and for
/// the same reason: no date and no URL is written as "".
/// </summary>
/// <param name="PullRequestUrl">
/// An absolute <c>http</c> or <c>https</c> URL, or <c>""</c> to take the issue
/// off the one it holds. Anything else is refused with a sentence - a relative
/// path or a bare <c>github.com/...</c> is a link that would not open, and the
/// whole point of the field is that it opens.
/// </param>
public record IssuePatchRequest(
    string? Title,
    string? Description,
    string? Type,
    int? StatusId,
    string? ParentKey,
    string? ReadyAt,
    string? DueAt,
    string? PullRequestUrl);

/// <summary>
/// A drop on the board: which column, and which cards it landed between. The
/// client names neighbours and never a rank - the server owns the number
/// (docs/hatch.md, "Rank computation"), which is what keeps every client
/// dumb, Claude included.
/// </summary>
/// <param name="AfterKey">The card immediately above the drop, or null at the top of the column.</param>
/// <param name="BeforeKey">The card immediately below it, or null at the bottom.</param>
public record IssueMoveRequest(int StatusId, string? AfterKey, string? BeforeKey);

// ---- Searching and editing in bulk ----

/// <summary>
/// What a bulk edit acted on, and what it refused. Never an exception and never
/// a partial-looking success: an issue whose edit could not be applied is named
/// here with the sentence saying why, and nothing about it was written.
/// </summary>
/// <param name="Changed">The keys that actually moved. An issue already holding every named value is not one of them.</param>
/// <param name="Unchanged">Keys that matched the request but had nothing to change - re-applying a bulk edit is not an edit.</param>
/// <param name="Failures">Keys the edit was refused for, each with its reason.</param>
public record IssueBulkResultDto(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<IssueBulkFailureDto> Failures);

public record IssueBulkFailureDto(string Key, string Reason);

/// <summary>
/// One edit applied to many issues. The fields are
/// <see cref="IssuePatchRequest"/>'s, minus the three - title, description and
/// pull request URL - that describe a single issue and could only be applied to
/// a hundred of them by mistake.
///
/// Null still means "leave this alone", and the empty string still clears, so
/// "take the due date off all of these" is <c>dueAt: ""</c> and nothing else.
/// </summary>
/// <param name="Keys">
/// The issues to edit, named rather than described. The filter that found them
/// is the client's business: a request that re-ran a query server-side could
/// act on a row that arrived between the operator reading the list and pressing
/// the button, which is the one thing a bulk edit must never do.
/// </param>
public record IssueBulkEditRequest(
    IReadOnlyList<string> Keys,
    string? Type = null,
    int? StatusId = null,
    string? ParentKey = null,
    string? ReadyAt = null,
    string? DueAt = null);

// ---- Comments and events ----

/// <param name="Kind">
/// <c>""</c> for an ordinary note, <c>"question"</c> or <c>"answer"</c> - see
/// <see cref="EfHatchComment.Kind"/> for why those two are a column.
/// </param>
/// <param name="AnswersId">The question this answers, on the same issue. Null on everything else.</param>
/// <param name="Options">
/// The answers a question offers, or null on one asked in prose. See
/// <see cref="EfHatchComment.Options"/>.
/// </param>
public record CommentDto(
    long Id,
    string Author,
    string Body,
    string Kind,
    long? AnswersId,
    IReadOnlyList<QuestionOptionDto>? Options,
    DateTimeOffset CreatedAt);

/// <summary>
/// One answer a question offers up front.
///
/// A label and a detail rather than one string, because they are read at
/// different moments: the label is what is scanned down a list and what becomes
/// the answer's own text, and the detail is what is read once, by somebody
/// deciding between two of them.
/// </summary>
/// <param name="Label">
/// The choice, as it will be said. Short enough to press and to read back in a
/// thread six months later - "child-weighted", not a sentence about weighting.
/// </param>
/// <param name="Detail">What taking it means, and what it costs. Optional; a self-evident choice does not need one.</param>
/// <param name="Recommended">
/// The one the asker would take. At most one per question - a recommendation
/// that covers two options is not a recommendation.
/// </param>
public record QuestionOptionDto(string Label, string? Detail = null, bool Recommended = false)
{
    public const int MaxLabelLength = 120;
    public const int MaxDetailLength = 2_000;

    /// <summary>
    /// Enough to cover a decision and few enough to read without scrolling. A
    /// question with nine answers is two questions.
    /// </summary>
    public const int MaxPerQuestion = 8;
}

/// <summary>
/// A new comment. <paramref name="Kind"/> is omitted by everything that just
/// wants to say something, which is most callers and every caller that predates
/// questions.
/// </summary>
/// <param name="Options">
/// Offered answers, on a question only. Omitted asks in prose, which stays
/// legal - not every decision is a menu.
/// </param>
public record CommentCreateRequest(
    string Body,
    string? Kind = null,
    long? AnswersId = null,
    IReadOnlyList<QuestionOptionDto>? Options = null);

/// <summary>
/// A question and whatever has been said back to it - what the CLI walks
/// through, what the issue page threads together, and what the board counts.
/// </summary>
/// <param name="IssueKey">
/// Carried on the question rather than looked up, because the house-wide list
/// is read by somebody answering several tickets in a row and a question
/// without its ticket is unanswerable.
/// </param>
/// <param name="Answers">Oldest first. Empty is what "open" means; there is no second flag saying so.</param>
/// <param name="Options">The answers it offers, or null if it was asked in prose.</param>
public record QuestionDto(
    long Id,
    string IssueKey,
    string IssueTitle,
    string Body,
    string AskedBy,
    DateTimeOffset AskedAt,
    IReadOnlyList<QuestionOptionDto>? Options,
    IReadOnlyList<CommentDto> Answers);

/// <param name="Payload">
/// <c>{ "from": …, "to": … }</c> for an edit, the source filename for an
/// import - served as JSON rather than as a string of JSON, so a client reads
/// it without a second parse.
/// </param>
public record IssueEventDto(long Id, string Actor, string Kind, JsonElement? Payload, DateTimeOffset At);

// ---- The board ----

/// <summary>
/// One request, the whole board: the columns in order and every card in the
/// house. Deliberately not paged - this is one household's work, and a board
/// that arrives in pieces cannot answer "what is in progress" in one glance.
/// </summary>
/// <remarks>
/// Every card, including the ones whose <see cref="IssueCardDto.ReadyAt"/> has
/// not arrived. The browser folds those behind a per-column count and the
/// server does not, because a default that silently drops rows leaves a client
/// unable to tell an empty board from a filtered one - and an agent asking what
/// it may work on has one comparison to make instead of a flag to know about.
/// </remarks>
public record BoardDto(IReadOnlyList<StatusDto> Statuses, IReadOnlyList<IssueCardDto> Issues);

// ---- The importer ----

/// <summary>
/// How far along an imported node is, before it is matched to a column. The
/// parser reads a shape and this names it; the import endpoint is the only
/// thing that knows which status row a shape lands in, because column names are
/// the operator's to change.
/// </summary>
public enum PlanState
{
    Todo,
    InProgress,
    Done,
}

/// <summary>One checkbox from a plan.</summary>
public record ParsedTask(string Title, string Description, PlanState State);

/// <summary>One <c>## Phase</c> section: its heading, its prose, and its checkboxes.</summary>
public record ParsedStory(string Title, string Description, PlanState State, IReadOnlyList<ParsedTask> Tasks);

/// <summary>
/// One uploaded plan file, as the issues it would become. Handed back by
/// <c>preview</c> and handed in again to <c>import</c> unchanged - so what the
/// operator approved on screen is exactly what gets written, with no second
/// parse in between to disagree with the first.
/// </summary>
public record ParsedEpic(
    string Filename,
    string Title,
    string Description,
    PlanState State,
    IReadOnlyList<ParsedStory> Stories);

/// <summary>
/// One plan typed or pasted straight into the page, rather than uploaded as a
/// file. The same document by the time the parser sees it - which is the point:
/// a plan that only ever existed in somebody's clipboard should reach the board
/// by the same road as one that lives in <c>docs/plans</c>, not by a second
/// implementation of the same reading.
/// </summary>
/// <param name="Title">
/// What this document is called. It plays the part a filename plays for an
/// upload: the name every imported issue's provenance line carries, and the
/// epic's own title when the body has no <c>#</c> heading of its own.
/// </param>
/// <param name="Body">The markdown, exactly as the file would have held it.</param>
public record PastedPlan(string Title, string Body);

/// <param name="Docs">The trees from <c>preview</c>, whichever of them the operator kept.</param>
public record ImportRequest(int ProjectId, IReadOnlyList<ParsedEpic> Docs);

/// <param name="Key">The epic's display key, so the result list can link to what it made.</param>
public record ImportedEpicDto(string Filename, string Key, string Title, int StoryCount, int TaskCount);

public record ImportResultDto(IReadOnlyList<ImportedEpicDto> Epics, int IssueCount);

// ---- Playbooks ----

/// <summary>One row of the matrix: a transition, the types it speaks for, and what to spend on them.</summary>
/// <param name="Types">Empty means every type.</param>
public record PlaybookDto(
    int Id,
    int FromStatusId,
    string FromStatusName,
    int ToStatusId,
    string ToStatusName,
    IReadOnlyList<string> Types,
    string Prompt,
    string Model,
    string Effort,
    DateTimeOffset UpdatedAt);

public record PlaybookCreateRequest(
    int FromStatusId,
    int ToStatusId,
    IReadOnlyList<string>? Types,
    string Prompt,
    string? Model,
    string? Effort);

/// <summary>Null leaves a field alone, as everywhere else in Hatch.</summary>
public record PlaybookPatchRequest(
    int? FromStatusId,
    int? ToStatusId,
    IReadOnlyList<string>? Types,
    string? Prompt,
    string? Model,
    string? Effort);

// ---- Work ----

/// <summary>
/// Everything a spawned agent needs to do one increment on one issue, decided
/// here rather than in the shell: which issue, which way it is going, what to
/// tell the agent, and how much thought to spend.
/// </summary>
/// <param name="Blocked">
/// Why no agent should be spawned, or null when one should. A sentence rather
/// than a code - it is printed at a terminal and read by a person.
/// </param>
/// <param name="ToStatus">Where the increment ends, or null when there is nowhere to go.</param>
/// <param name="Playbook">The matched row, or null when the matrix says nothing about this transition.</param>
/// <param name="Questions">
/// Every question ever asked about this issue, answered and not. Carried on the
/// dispatch rather than fetched separately because both halves are needed here
/// and for opposite reasons: an answered question is a decision the next
/// session must not re-open, and an open one is why there is no next session
/// yet - see <paramref name="Blocked"/>.
/// </param>
public record WorkDto(
    IssueDto Issue,
    StatusDto FromStatus,
    StatusDto? ToStatus,
    PlaybookDto? Playbook,
    IReadOnlyList<IssueCardDto> Children,
    IReadOnlyList<QuestionDto> Questions,
    string? Blocked);

/// <summary>
/// One row of a pass: an issue the dispatcher looked at, and what it decided
/// about it.
/// </summary>
/// <remarks>
/// The fields <see cref="WorkDto"/> carries, minus the playbook prompt, the
/// children and the questions. Those three are the payload of a dispatch - one
/// agent, one issue - and loading them for every row would make a whole-board
/// read expensive for nothing, since a scan is read to find out what was
/// skipped and not to do the work.
/// </remarks>
/// <param name="Blocked">
/// Why the pass folded past this issue, or null where it did not. The first
/// entry with a null <c>Blocked</c> is the issue <c>work/next</c> returns for
/// the same arguments, because it is the same walk.
/// </param>
public record QueueEntryDto(
    IssueDto Issue,
    StatusDto FromStatus,
    StatusDto? ToStatus,
    string? Blocked);

// ---- Rollups ----

/// <summary>
/// One column's share of a subtree: how many of its leaves are sitting there.
/// </summary>
/// <remarks>
/// A status no leaf is in is absent rather than present with a zero - the
/// client already holds the column list and does not need a row that draws
/// nothing.
/// </remarks>
public record RollupSliceDto(int StatusId, int Count);

/// <summary>
/// What a subtree adds up to: the arithmetic every meter is drawn from, done
/// once on the server so the Plan page, the issue page and anything holding an
/// API key all read the same number. See <see cref="Rollup"/> for what a leaf
/// is and why it is the unit.
/// </summary>
/// <param name="Leaves">
/// The total the slices sum to - the number of issues with no children beneath
/// this one, or one when this issue is a leaf itself.
/// </param>
/// <param name="Done">Leaves sitting in a terminal column. The numerator of "how far along is this".</param>
/// <param name="Waiting">
/// Open questions on this issue and every descendant, at any depth. What says
/// an epic is blocked on a person rather than on an agent, counted through
/// <see cref="Questions.OpenCountsAsync"/> so there is still one definition of
/// "open".
/// </param>
/// <param name="Slices">In board order (<c>sortOrder</c>, then id), empty columns absent.</param>
public record RollupDto(int Leaves, int Done, int Waiting, IReadOnlyList<RollupSliceDto> Slices);

/// <summary>One direct child of the issue asked about, with its own rollup.</summary>
/// <param name="IsLeaf">
/// Whether it has children of its own - the flag that tells a client to draw a
/// status pill rather than a bar. A field rather than something inferred from
/// <see cref="RollupDto.Leaves"/> being one, because a story with a single task
/// and a task with none must not look alike on the wire.
/// </param>
public record ChildRollupDto(IssueCardDto Issue, bool IsLeaf, RollupDto Rollup);

/// <summary>
/// One subtree and the row under it: what the issue page draws beneath an epic
/// or a story. The children are the direct ones, in rank order, each carrying
/// the rollup of everything beneath <em>it</em> - so a stack of meters agrees
/// with the one above it by construction.
/// </summary>
public record IssueRollupDto(string Key, RollupDto Rollup, IReadOnlyList<ChildRollupDto> Children);

/// <summary>
/// One epic on the Plan page: the card, what everything beneath it adds up to,
/// and the epics beneath it drawn the same way.
/// </summary>
/// <param name="Rollup">
/// The whole subtree, not only the epics in <paramref name="Children"/> - every
/// story, task and bug under it at any depth. An epic's meter would otherwise
/// read as empty until somebody filed an epic inside it.
/// </param>
/// <param name="Children">
/// The epics below this one, each appearing exactly here and not again at the
/// top level, so the page draws the tree once. Ordered by key.
/// </param>
public record PlanEntryDto(IssueCardDto Issue, bool IsLeaf, RollupDto Rollup, IReadOnlyList<PlanEntryDto> Children);

/// <summary>
/// The landscape in one request: every epic in the tracker with what it adds up
/// to, so the Plan page is a list of meters rather than a question per bar.
/// </summary>
/// <param name="Epics">The epics with no parent, each carrying the epics beneath it. Ordered by key.</param>
/// <param name="Loose">
/// The work that hangs under no epic at all - the leaves below every root issue
/// that is not an epic, and <c>leaves: 0</c> when there is none. It is here so
/// that the Plan page cannot quietly become a view that hides half the tracker:
/// an operator who files a story without a parent should be able to see that
/// they did.
/// </param>
public record PlanDto(IReadOnlyList<PlanEntryDto> Epics, RollupDto Loose);
