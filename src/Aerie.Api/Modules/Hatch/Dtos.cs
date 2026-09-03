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
/// <see cref="EfHatchProject.KeyPattern"/> and against every existing key, and
/// there is deliberately no way to change it afterwards - see
/// <see cref="EfHatchProject.Key"/>.
/// </summary>
public record ProjectCreateRequest(string Key, string Name);

/// <summary>The name, and only the name. The key is immutable by design.</summary>
public record ProjectPatchRequest(string Name);

// ---- Statuses ----

public record StatusDto(int Id, string Name, int SortOrder, bool IsTerminal);

public record StatusCreateRequest(string Name, int? SortOrder, bool? IsTerminal);

/// <summary>Every field optional: null means "leave this one alone".</summary>
public record StatusPatchRequest(string? Name, int? SortOrder, bool? IsTerminal);

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
public record IssueCardDto(
    string Key,
    string ProjectKey,
    string Type,
    string Title,
    int StatusId,
    long Rank,
    string? ParentKey,
    string? ReadyAt,
    string? DueAt);

/// <summary>One issue, whole - the detail page's payload.</summary>
/// <param name="ChildKeys">Its stories, or its tasks. Keys rather than nested issues: the page links to them and does not draw them.</param>
/// <param name="ReadyAt">
/// When the issue becomes workable, or null if it always was. A bare date
/// (<c>2026-09-12</c>) or an instant (<c>2026-09-12T17:00:00Z</c>) - the two
/// forms mean different things and <see cref="IssueMoment"/> says how.
/// </param>
/// <param name="DueAt">When it is owed, in the same two forms, or null.</param>
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
/// <paramref name="ReadyAt"/> and <paramref name="DueAt"/> read the empty
/// string the same way, and for the same reason: no date is written as "".
/// </summary>
public record IssuePatchRequest(
    string? Title,
    string? Description,
    string? Type,
    int? StatusId,
    string? ParentKey,
    string? ReadyAt,
    string? DueAt);

/// <summary>
/// A drop on the board: which column, and which cards it landed between. The
/// client names neighbours and never a rank - the server owns the number
/// (docs/plans/pjm.md, "Rank computation"), which is what keeps every client
/// dumb, Claude included.
/// </summary>
/// <param name="AfterKey">The card immediately above the drop, or null at the top of the column.</param>
/// <param name="BeforeKey">The card immediately below it, or null at the bottom.</param>
public record IssueMoveRequest(int StatusId, string? AfterKey, string? BeforeKey);

// ---- Comments and events ----

public record CommentDto(long Id, string Author, string Body, DateTimeOffset CreatedAt);

public record CommentCreateRequest(string Body);

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
