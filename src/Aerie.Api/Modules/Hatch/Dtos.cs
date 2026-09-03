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
public record IssueCardDto(
    string Key,
    string ProjectKey,
    string Type,
    string Title,
    int StatusId,
    long Rank,
    string? ParentKey);

/// <summary>One issue, whole - the detail page's payload.</summary>
/// <param name="ChildKeys">Its stories, or its tasks. Keys rather than nested issues: the page links to them and does not draw them.</param>
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
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A new issue. It lands in the leftmost status and at the bottom of that
/// column - the server decides both, so no client has to know what "the inbox"
/// is called this week.
/// </summary>
public record IssueCreateRequest(int ProjectId, string Type, string Title, string? Description, string? ParentKey);

/// <summary>
/// An edit. Every field is optional and null means "leave this alone", which
/// leaves one thing that needs saying out loud: an empty
/// <paramref name="ParentKey"/> - <c>""</c> - clears the parent. A JSON body
/// cannot otherwise distinguish "no opinion" from "no parent", and the empty
/// string is unambiguous because no issue key can ever be one.
/// </summary>
public record IssuePatchRequest(
    string? Title,
    string? Description,
    string? Type,
    int? StatusId,
    string? ParentKey);

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
public record BoardDto(IReadOnlyList<StatusDto> Statuses, IReadOnlyList<IssueCardDto> Issues);
