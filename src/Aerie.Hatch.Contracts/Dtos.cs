using System.Text.Json;

namespace Aerie.Hatch.Contracts;

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
/// Who an issue belongs to: a person, or an API key, or - said as
/// <c>null</c> wherever this appears - nobody at all.
/// </summary>
/// <remarks>
/// <para>Hatch's own wire shape built from
/// <see cref="Aerie.Api.Services.Auth.Actor"/>, so the module's payload stays
/// the module's and the platform record can grow a field without changing what
/// a board read looks like.</para>
///
/// <para>Null is the only way "nobody" is said: there is no empty-object form,
/// so a client's test is <c>assignee &amp;&amp; …</c> and never a comparison
/// against a sentinel id. An assignee whose person has been deleted or whose
/// key has been revoked reads as null too, at every reader at once - see
/// <see cref="Aerie.Api.Services.Auth.IActorDirectory"/>.</para>
/// </remarks>
/// <param name="Kind"><c>person</c> or <c>key</c> - see <see cref="Aerie.Api.Services.Auth.ActorKind"/>.</param>
public record AssigneeDto(string Kind, Guid Id, string Name);

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
/// <param name="Assignee">
/// Who owns this card, or null for nobody - which is most of the board, and
/// which is why the card draws no element at all rather than an empty chip.
/// Trailing and defaulted for the reason <paramref name="OpenQuestions"/> is,
/// though every list that draws a card fills it: an assignee that read one way
/// through the board and another through the plan is the divergence this record
/// exists to prevent.
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
    int OpenQuestions = 0,
    AssigneeDto? Assignee = null,
    IssueClaimDto? Claim = null);

/// <summary>
/// The lease a running dispatcher holds on an issue, or null where nothing
/// holds it - see <see cref="EfHatchIssue.ClaimToken"/>. An expired claim
/// projects as null rather than as a claim with an old heartbeat: the
/// arithmetic is the server's, and a card drawing a holder that stopped
/// existing four hours ago is worse than a card drawing nothing.
/// </summary>
/// <remarks>
/// There is deliberately no token here. The token is a capability - it is what
/// a heartbeat and a release present - and a board read that carried it would
/// let anybody holding a board read steal or refresh a lease. What a client
/// needs is who, from where, and how recently, which is all of this.
/// </remarks>
/// <param name="Chatter">A line the holder is carrying: what it is doing right now, or null if it has not said.</param>
public record IssueClaimDto(
    string ClaimedBy,
    string Runner,
    DateTimeOffset ClaimedAt,
    DateTimeOffset HeartbeatAt,
    string? Chatter,
    DateTimeOffset? ChatterAt,
    /// <summary>
    /// The lease this claim is judged against, in seconds - the same value
    /// <see cref="ClaimTakenDto"/> carries, on the read as well as on the take.
    /// Here rather than beside the claim so that a claim is self-describing
    /// wherever one is drawn: a card on the board, a child row on the plan, a
    /// section on the issue page. A client that had to fetch the TTL from
    /// somewhere else could draw a claim before it knew what "recently" meant.
    ///
    /// It is not a second copy of the rule. <see cref="IssueClaims.IsLive"/>
    /// still decides whether there is a claim at all, and this only says how
    /// much of the lease there was to spend - which is what lets a client draw
    /// a runner that has gone quiet without deciding for itself when one is
    /// gone.
    /// </summary>
    int TtlSeconds);

/// <summary>One issue, whole - the detail page's payload.</summary>
/// <param name="ChildKeys">Its stories, or its tasks. Keys rather than nested issues: the page links to them and does not draw them.</param>
/// <param name="DependsOnKeys">
/// What must be done before this is implemented, in key order. An edge is
/// satisfied only once the issue it names sits in a terminal column, so a
/// blocker in review still blocks - anything softer and the second story is
/// built on the first one's unmerged branch.
/// </param>
/// <param name="DependentKeys">
/// The issues waiting on this one - the same edges read backwards. Named for
/// what they are rather than "blocked", which has two readings.
/// </param>
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
/// <param name="ModelOverride">
/// The model every increment dispatched for this issue runs on, or null for
/// whatever the playbook for its next move names. Readable by a key - an agent
/// may see what it is being spent on - and writable only by a person, through
/// <see cref="IssuePlaybookController"/>.
/// </param>
/// <param name="EffortOverride">The same, for the thinking budget, and independent of it.</param>
/// <param name="Assignee">
/// Who owns it, or null for nobody. Beside <paramref name="CreatedBy"/> because
/// the two are the same kind of fact - who filed it, and whose it is now -
/// though only one of them can change. Readable by a key, and writable only by
/// a person through <see cref="AssigneeController"/>: under the loop's
/// <c>people only</c> rule an assignee is a dispatch gate, and a key that could
/// write one could hand itself work somebody had reserved.
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
    IReadOnlyList<string> DependsOnKeys,
    IReadOnlyList<string> DependentKeys,
    string? ReadyAt,
    string? DueAt,
    string? PullRequestUrl,
    string? ModelOverride,
    string? EffortOverride,
    AssigneeDto? Assignee,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IssueClaimDto? Claim = null);

/// <summary>Taking the lease: who is asking is the credential's to say, so the body names only where from.</summary>
/// <param name="Runner">The checkout holding it - <c>host:/path/to/checkout</c>, as the runner names itself.</param>
public record ClaimRequest(string Runner)
{
    /// <summary>
    /// The longest runner the server will accept. Here rather than on the
    /// entity because both sides of the wire need it: the API refuses a longer
    /// one, and the runner shortens its own name to fit rather than discovering
    /// the limit as a 400 that stops a night's loop.
    /// </summary>
    public const int MaxRunnerLength = 240;

    /// <summary>
    /// The longest line a claim will carry. The server truncates rather than
    /// refusing - a lease is not worth losing to a wide terminal - so this is
    /// the length past which a client is writing for nobody.
    /// </summary>
    public const int MaxChatterLength = 512;
}

/// <summary>
/// A lease, just taken. The TTL rides back with it rather than being configured
/// on both sides: the server is what honours it, so the server is what says
/// what it is.
/// </summary>
/// <param name="Token">
/// The capability. Presented by every heartbeat and by the release, and the
/// only place it is ever handed out - it is on no read anywhere.
/// </param>
public record ClaimTakenDto(Guid Token, string ClaimedBy, DateTimeOffset ClaimedAt, int TtlSeconds);

/// <summary>
/// Still here. The token is the whole authorization; the line is optional and
/// cosmetic.
/// </summary>
/// <param name="Chatter">
/// What the holder is doing right now. Absent leaves whatever the claim was
/// carrying alone, and <c>""</c> clears it. Trimmed to its first line and
/// truncated rather than refused - a lease is not worth losing to a wide
/// terminal.
/// </param>
public record ClaimHeartbeatRequest(Guid Token, string? Chatter);

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

/// <summary>
/// What one issue overrides its playbooks with. Null leaves a field alone and
/// <c>""</c> hands it back to the playbook, as everywhere else in Hatch; the
/// two fields are independent, so an issue may carry a model and no effort.
/// </summary>
/// <remarks>
/// Its own request, and its own route, because
/// <see cref="IssuesController"/> accepts the <c>hatch</c> scope and this must
/// not - see <see cref="IssuePlaybookController"/>. There is no prompt here on
/// purpose: a prompt is the <em>method</em> for a transition, and a per-issue
/// method is a paragraph, which is what a description is.
/// </remarks>
public record IssuePlaybookRequest(string? Model, string? Effort);

/// <summary>
/// Who an issue is to belong to. Both fields null is the unassign; exactly one
/// of them is refused with a sentence, because a kind with no id is a request
/// that meant something and did not say what.
/// </summary>
/// <remarks>
/// What you read is what you write: this is <see cref="AssigneeDto"/> minus the
/// name, so no client has to learn two shapes for one fact. The name is the
/// directory's to say and not a caller's to assert - a request naming a person
/// "Nathan" who is really somebody else would be a second source of truth about
/// a row this process owns.
/// </remarks>
public record AssigneeRequest(string? Kind, Guid? Id);

/// <summary>
/// The picker's rows and the answer to "who am I", in one read.
/// </summary>
/// <remarks>
/// One request rather than two because <em>Assign to me</em> needs both and a
/// browser that inferred the second from a cookie would be a second
/// implementation of who the caller is. <paramref name="Me"/> is null where
/// nobody is signed in, which is the ordinary state of local development with
/// <c>Auth:Enabled</c> false - and the press is simply absent there.
/// </remarks>
/// <param name="Assignees">
/// Every person and every live key, people first and each A→Z. A revoked key is
/// not in it, which is the same predicate that makes an issue already pointing
/// at one read as unassigned.
/// </param>
public record AssigneeDirectoryDto(AssigneeDto? Me, IReadOnlyList<AssigneeDto> Assignees);

/// <summary>
/// One edge: this issue waits on <paramref name="DependsOnKey"/>.
/// </summary>
/// <remarks>
/// Unlike a playbook or a per-issue override, writing one of these is open to a
/// key. A dependency is a statement about the work rather than about an agent's
/// budget, and a planning session that has just filed five stories is exactly
/// who should chain them.
/// </remarks>
public record IssueDependencyRequest(string DependsOnKey);

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

// ---- The work log ----

/// <summary>
/// What one model cost inside one session - the row's breakdown, one entry per
/// model the session used.
/// </summary>
/// <remarks>
/// Hatch's names, not Anthropic's. The CLI's <c>result</c> event spells these
/// <c>inputTokens</c>, <c>cacheCreationInputTokens</c>, <c>costUSD</c> and so
/// on, and the translation happens once, in the runner's stream renderer
/// (<c>src/Aerie.Hatch/StreamRender.cs</c>), exactly as
/// <c>ClaudeUsageClient</c> is the only file that knows the battery's spelling.
/// Nothing downstream of the wire should have to know two vocabularies.
/// </remarks>
public record WorkLogModelUseDto(
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    decimal CostUsd);

/// <summary>
/// One finished session, as the dispatcher reports it.
/// </summary>
/// <remarks>
/// Two fields on the stored row are deliberately absent here, and both for the
/// same reason: they are derived, and a caller that could send them is a caller
/// that could make the row disagree with itself.
///
/// The four token counts are the sum over <paramref name="Models"/>, computed by
/// the endpoint. And <c>Described</c> is whether a title or a summary actually
/// arrived - a run that died before it could say what it did still gets its row,
/// with every metric intact and the mark on the wire rather than left for a
/// client to infer from an empty string.
/// </remarks>
/// <param name="SessionId">What <c>claude --resume</c> takes.</param>
/// <param name="StartedAt">Wall clock at the spawn.</param>
/// <param name="EndedAt">Wall clock as the stream closed.</param>
/// <param name="DurationMs">The session's own <c>duration_ms</c>, which is the smaller number and the honest one.</param>
/// <param name="Title">What the session did, in a few words, or null when it never said.</param>
/// <param name="Summary">The same at length. Clipped rather than refused when it runs long - see <see cref="IssueWorkLogController"/>.</param>
/// <param name="Models">
/// The per-model breakdown. Absent on a run that fell over before the accounting
/// arrived, which records zero tokens and whatever cost was reported: a session
/// that ended badly still had an id and still cost something.
/// </param>
public record WorkLogEntryRequest(
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long DurationMs,
    string? Title,
    string? Summary,
    bool IsError,
    int Turns,
    decimal CostUsd,
    IReadOnlyList<WorkLogModelUseDto>? Models);

/// <summary>
/// One row of the work log, as the issue page draws it.
/// </summary>
/// <param name="Described">
/// Whether the session said what it did. On the wire rather than inferred from
/// an empty title, because "this run never described itself" is a fact about the
/// run and a client deducing it from an absent string is a client guessing.
/// </param>
/// <param name="TotalTokens">
/// The four counts added up, carried rather than left to the client, so the
/// headline figure has one definition.
/// </param>
public record WorkLogEntryDto(
    long Id,
    string SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long DurationMs,
    string? Title,
    string? Summary,
    bool Described,
    bool IsError,
    int Turns,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens,
    IReadOnlyList<WorkLogModelUseDto> Models);

/// <summary>
/// What a set of sessions cost, added up. See <see cref="WorkLogRollup"/> for
/// why this addition is not the leaf rule <see cref="Rollup"/> uses.
/// </summary>
/// <param name="Sessions">How many rows are in the sum.</param>
/// <param name="Errors">How many of them ended badly. Their spend is in the totals either way.</param>
/// <param name="TotalTokens">The four counts added up - one definition of the headline, as on an entry.</param>
public record WorkLogTotalsDto(
    int Sessions,
    int Errors,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens,
    decimal CostUsd);

/// <summary>
/// An issue's work log: what everything beneath it has cost, what it has cost
/// on its own, and its own sessions.
/// </summary>
/// <remarks>
/// The asymmetry is the point and is worth saying out loud: <b>entries are the
/// issue's own, totals are the subtree's.</b> An epic showing four sessions of
/// its own and 1.4M tokens is not a bug, it is the feature - and
/// <paramref name="Own"/> is here so a page can say which number is which
/// instead of letting one pass for the other.
/// </remarks>
/// <param name="Totals">This issue and every descendant, at any depth.</param>
/// <param name="Own">Only the entries on this issue.</param>
/// <param name="Entries">This issue's own sessions, newest first.</param>
public record WorkLogDto(
    string Key,
    WorkLogTotalsDto Totals,
    WorkLogTotalsDto Own,
    IReadOnlyList<WorkLogEntryDto> Entries);

/// <summary>
/// One equal slice of the work log's time axis, and what ended inside it.
/// </summary>
/// <remarks>
/// Half-open: a session belongs to the bucket with
/// <c>Start &lt;= EndedAt &lt; End</c>, so it is counted once and the buckets add
/// up to the range's own total.
///
/// A bucket with no sessions in it carries a zeroed
/// <see cref="WorkLogTotalsDto"/> rather than being left out, and that is the
/// rule worth stating: an hour in which nothing ran is an hour that cost
/// nothing, which is a measurement. A poll's history has gaps because a missing
/// reading means nobody looked; a work log has none.
/// </remarks>
public record WorkLogBucketDto(DateTimeOffset Start, DateTimeOffset End, WorkLogTotalsDto Totals);

/// <summary>
/// What the work log recorded over a range of time, in equal buckets - the
/// shape a graph is drawn from.
/// </summary>
/// <remarks>
/// <paramref name="From"/> and <paramref name="To"/> are the requested range
/// snapped outward onto whole buckets, so the range, the bucket list and
/// <paramref name="Totals"/> all describe one window and the totals are the sum
/// of the buckets by construction.
/// </remarks>
/// <param name="Bucket">The size actually used, <c>hour</c> or <c>day</c> - which may be one the server chose rather than one the caller asked for.</param>
/// <param name="Totals">The whole range, so a caller does not have to add the buckets up.</param>
/// <param name="FirstSessionAt">
/// The earliest session in the filtered log, <em>ignoring the range</em>, or
/// null when nothing has ever run under this filter. It is what lets a page
/// choose a sensible range, and tell "nothing has run yet" apart from "nothing
/// ran in the range you asked for".
/// </param>
/// <param name="LastSessionAt">The latest, read the same way.</param>
/// <param name="Buckets">Oldest first, one per bucket in the range, empty ones included.</param>
public record WorkLogHistoryDto(
    DateTimeOffset From,
    DateTimeOffset To,
    string Bucket,
    WorkLogTotalsDto Totals,
    DateTimeOffset? FirstSessionAt,
    DateTimeOffset? LastSessionAt,
    IReadOnlyList<WorkLogBucketDto> Buckets);

/// <summary>
/// One agent session as the leaderboard reads it: what it was run against, what
/// it said about itself, and what it cost.
/// </summary>
/// <remarks>
/// Deliberately narrower than <see cref="WorkLogEntryDto"/> in two places, and
/// both absences are the point. <c>Summary</c> is up to 2000 characters and this
/// answers a hundred rows - the issue page is where a session is read at length.
/// <c>Models</c> is a list per row and nothing on the leaderboard draws a
/// per-model breakdown.
/// </remarks>
/// <param name="Title">What the session said it did, or null when it never said - see <paramref name="Described"/>.</param>
/// <param name="TotalTokens">The four counts added up - one definition of the headline, as everywhere else in the module.</param>
public record WorkLogSessionDto(
    long Id,
    string SessionId,
    string IssueKey,
    string IssueTitle,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long DurationMs,
    string? Title,
    bool Described,
    bool IsError,
    int Turns,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    long TotalTokens);

/// <summary>
/// The sessions in a range, ranked - and what that whole range cost, whether or
/// not every row of it came back.
/// </summary>
/// <remarks>
/// <paramref name="Totals"/> covers <b>the whole filter and not the returned
/// page</b>, which is what lets a capped table add up honestly; a caller tells
/// the two apart by comparing <c>Totals.Sessions</c> with the length of
/// <paramref name="Sessions"/> and says so on screen.
///
/// <paramref name="From"/> and <paramref name="To"/> are the range as given.
/// Nothing is snapped here, deliberately unlike <see cref="WorkLogHistoryDto"/>:
/// there is no bucket grid on this read to align to.
/// </remarks>
/// <param name="Sort">The sort as resolved - <c>tokens</c>, <c>cost</c> or <c>ended</c>.</param>
/// <param name="FirstSessionAt">
/// The earliest session in the population <c>ancestorKey</c> names,
/// <em>ignoring the range</em>, or null when that population is empty. Read
/// exactly as <see cref="WorkLogHistoryDto.FirstSessionAt"/> is, and here so a
/// page can tell "nothing has ever been logged" from "nothing ran in the range
/// you asked for" without fetching the graph's data to say it.
/// </param>
/// <param name="LastSessionAt">The latest, read the same way.</param>
public record WorkLogSessionsDto(
    DateTimeOffset From,
    DateTimeOffset To,
    string Sort,
    WorkLogTotalsDto Totals,
    DateTimeOffset? FirstSessionAt,
    DateTimeOffset? LastSessionAt,
    IReadOnlyList<WorkLogSessionDto> Sessions);

// ---- Runners ----

/// <summary>
/// What a runner is: a loop that will ask again, or a single increment that
/// will not.
/// </summary>
/// <remarks>
/// Spelled on the contract rather than on either side of the wire, because both
/// sides act on it: the runner says which it is, and the board decides from it
/// whether to draw a control surface at all.
/// </remarks>
public static class RunnerKinds
{
    /// <summary><c>go-to-work</c>: it heartbeats every pass and obeys what comes back.</summary>
    public const string Loop = "loop";

    /// <summary><c>work</c>, and <c>go-to-work --once</c>: one heartbeat, and no second pass to apply an instruction to.</summary>
    public const string Once = "once";
}

/// <summary>
/// What the board would like a runner to do: carry on, hold, or finish and
/// stop.
/// </summary>
/// <remarks>
/// Three and no more, and none of them is "kill it". Nothing on the server
/// reaches into a process; every one of these is picked up by the loop itself,
/// between increments, which is what makes them work for a runner behind a
/// router nothing can reach.
/// </remarks>
public static class RunnerStates
{
    /// <summary>Take the next ticket, as ever.</summary>
    public const string Running = "running";

    /// <summary>Keep saying you are here; take nothing.</summary>
    public const string Paused = "paused";

    /// <summary>Finish whatever is in flight and exit without picking another.</summary>
    public const string Stopping = "stopping";

    /// <summary>The three, in the order a control surface should offer them.</summary>
    public static readonly string[] All = [Running, Paused, Stopping];
}

/// <summary>
/// One runner as the board draws it: who it is, what it is doing, when it was
/// last heard from, and what it has been asked to do next.
/// </summary>
/// <remarks>
/// Nothing here is a second copy of the claim. <paramref name="ClaimKey"/> and
/// the line beside it are read live off whichever issue this runner holds at
/// the moment the request is served, so an operator who clears a claim sees the
/// runner go idle on the next poll rather than seeing a column that remembers
/// the ticket.
/// </remarks>
/// <param name="Name">What the runner calls itself - <c>host:/path/to/checkout</c>, the same string its claims carry.</param>
/// <param name="Kind">
/// <c>loop</c> for <c>go-to-work</c>, which asks for instructions between
/// increments, or <c>once</c> for a single increment that will never read one.
/// A control surface is worth drawing only for the first.
/// </param>
/// <param name="ClaimKey">The issue it holds right now, or null between increments.</param>
/// <param name="Line">
/// The last thing it said: the claim's chatter while it holds one, and its own
/// last line when it does not. One field because it is one question - what is
/// this runner doing - and which row answered it is not the reader's problem.
/// </param>
/// <param name="GoneAfterSeconds">
/// How long a runner may go unheard from before it is gone, the way a claim
/// carries its own TTL: the server honours it, so the server says what it is,
/// and a client can draw "quiet" without deciding for itself what quiet means.
/// </param>
public record RunnerDto(
    string Name,
    string Kind,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string? ClaimKey,
    string? Line,
    DateTimeOffset? LineAt,
    string State,
    string? Under,
    int? MaxRuns,
    decimal? MaxSpend,
    DateTimeOffset? UntilAt,
    int GoneAfterSeconds);

/// <summary>
/// Still here, and what should I do next - the one call a runner makes about
/// itself.
/// </summary>
/// <remarks>
/// The four bounds are <em>reported</em>, not requested: they are what this
/// process started with, and they are written only when the row is being
/// created. A restart of the same loop sends them again and the server ignores
/// them, which is what stops a restart from undoing an operator's edit.
/// </remarks>
/// <param name="Kind">
/// <see cref="RunnerDto.Kind"/>, and the runner is what knows it: a loop asks
/// again, a single increment does not.
/// </param>
/// <param name="Line">
/// What it is doing right now, on the terms
/// <see cref="ClaimHeartbeatRequest.Chatter"/> has: absent leaves the last one
/// alone, <c>""</c> clears it, and anything longer than
/// <see cref="ClaimRequest.MaxChatterLength"/> is truncated rather than
/// refused.
/// </param>
public record RunnerHeartbeatRequest(
    string? Kind = null,
    string? Line = null,
    string? Under = null,
    int? MaxRuns = null,
    decimal? MaxSpend = null,
    DateTimeOffset? UntilAt = null);

/// <summary>
/// What the board would like this runner to do, answered to its own heartbeat
/// and read by nobody else.
/// </summary>
/// <remarks>
/// The loop folds all five into its bounds before its next pick, so a person
/// editing a row changes what that loop does from the next ticket onward -
/// never in the middle of one. That is not a check anywhere: the heartbeat
/// happens at the top of a pass, which is the one moment no claim is held.
/// </remarks>
public record RunnerInstructionDto(
    string State,
    string? Under,
    int? MaxRuns,
    decimal? MaxSpend,
    DateTimeOffset? UntilAt);

/// <summary>
/// The operator's half: keep going, pause, stop after this one - and the bounds
/// that were flags on the command line.
/// </summary>
/// <remarks>
/// Every field is a string on the wire, and absent / <c>""</c> / a value are
/// leave alone / clear / set, which is the convention the two dates on
/// <see cref="IssuePatchRequest"/> already established. The numbers wear the
/// same clothes rather than inventing a second tri-state for themselves - a
/// nullable number can say "leave it" or "set it" but not "take the cap off".
/// </remarks>
/// <param name="State">One of <c>running</c>, <c>paused</c>, <c>stopping</c>. Not clearable - a runner is always in one of the three.</param>
/// <param name="UntilAt">An instant (<c>2026-09-12T17:00:00Z</c>), read by <c>IssueMoment</c> the way a ready date is.</param>
public record RunnerPatchRequest(
    string? State = null,
    string? Under = null,
    string? MaxRuns = null,
    string? MaxSpend = null,
    string? UntilAt = null);
