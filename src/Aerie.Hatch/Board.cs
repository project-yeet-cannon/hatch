namespace Aerie.Hatch;

/// <summary>
/// The reads and writes a runner makes about a ticket, named once so that no
/// call site spells a route.
/// </summary>
/// <remarks>
/// Every one of these writes an event carrying the key's name as the actor, so
/// the trail says who did what without anybody being asked to record it. The
/// work log is the deliberate exception - a meter reading is not a decision.
/// </remarks>
public sealed class Board(HatchClient client)
{
    public HatchClient Client { get; } = client;

    /// <summary>
    /// This machine's offset from UTC in minutes, which is what the API wants in
    /// order to fold ready dates against the caller's calendar day rather than
    /// the server's.
    /// </summary>
    public static int OffsetMinutes(DateTimeOffset now) => (int)now.Offset.TotalMinutes;

    /// <summary>
    /// Every issue a pass would look at, in the order it looks, each with the
    /// reason it would be folded past - or nothing, where it is clear.
    /// </summary>
    public async Task<IReadOnlyList<QueueEntryDto>> QueueAsync(string? under, int offsetMinutes, CancellationToken ct)
    {
        var scope = string.IsNullOrEmpty(under) ? "" : $"&ancestorKey={Uri.EscapeDataString(under)}";
        return await Client.GetAsync<List<QueueEntryDto>>(
            $"/api/hatch/work/queue?offsetMinutes={offsetMinutes}{scope}", ct) ?? [];
    }

    /// <summary>
    /// The dispatch for one named issue.
    /// </summary>
    /// <param name="heldToken">
    /// The claim this runner is holding, so that its own lease does not fold its
    /// own dispatch. Absent everywhere a caller holds nothing.
    /// </param>
    public Task<WorkDto?> WorkAsync(string key, Guid? heldToken, CancellationToken ct)
    {
        var held = heldToken is { } token ? $"?heldToken={token}" : "";
        return Client.GetAsync<WorkDto>($"/api/hatch/work/{key}{held}", ct);
    }

    /// <summary>
    /// What a pass would take, without taking it.
    /// </summary>
    /// <remarks>
    /// The one caller left is <c>work --dry-run</c>, which spawns nothing and so
    /// holds nothing - and a walk that claimed in order to print a prompt would
    /// be the one thing a dry run must not do. Everywhere an increment actually
    /// follows, the queue picks and the claim decides; see <see cref="Picker"/>
    /// for why the second read there is the named one.
    /// </remarks>
    public Task<WorkDto?> NextAsync(string? under, int offsetMinutes, CancellationToken ct)
    {
        var scope = string.IsNullOrEmpty(under) ? "" : $"&ancestorKey={Uri.EscapeDataString(under)}";
        return Client.GetAsync<WorkDto>($"/api/hatch/work/next?offsetMinutes={offsetMinutes}{scope}", ct);
    }

    public async Task<IReadOnlyList<QuestionDto>> QuestionsAsync(string? key, bool open, CancellationToken ct)
    {
        var path = string.IsNullOrEmpty(key)
            ? $"/api/hatch/questions?open={open.ToString().ToLowerInvariant()}"
            : $"/api/hatch/issues/{key}/questions?open={open.ToString().ToLowerInvariant()}";

        return await Client.GetAsync<List<QuestionDto>>(path, ct) ?? [];
    }

    public async Task<IReadOnlyList<IssueCardDto>> UnderAsync(string ancestorKey, CancellationToken ct) =>
        await Client.GetAsync<List<IssueCardDto>>(
            $"/api/hatch/issues?ancestorKey={Uri.EscapeDataString(ancestorKey)}", ct) ?? [];

    public Task<CommentDto?> CommentAsync(string key, string body, CancellationToken ct) =>
        Client.PostAsync<CommentDto>($"/api/hatch/issues/{key}/comments", new CommentCreateRequest(body), ct);

    public Task<CommentDto?> AskAsync(
        string key, string body, IReadOnlyList<QuestionOptionDto> options, CancellationToken ct) =>
        Client.PostAsync<CommentDto>(
            $"/api/hatch/issues/{key}/comments",
            new CommentCreateRequest(body, Kind: "question", Options: options.Count > 0 ? options : null),
            ct);

    public Task<WorkLogEntryDto?> WorkLogAsync(string key, WorkLogEntryRequest entry, CancellationToken ct) =>
        Client.PostAsync<WorkLogEntryDto>($"/api/hatch/issues/{key}/work-log", entry, ct);

    // ---- The conversational half, which used to be jq over curl ----

    /// <summary>The columns and every card on them, in the board's own order.</summary>
    public Task<BoardDto?> BoardAsync(CancellationToken ct) =>
        Client.GetAsync<BoardDto>("/api/hatch/board", ct);

    /// <summary>The columns alone, which is what <c>show</c> needs to name one.</summary>
    public async Task<IReadOnlyList<StatusDto>> StatusesAsync(CancellationToken ct) =>
        await Client.GetAsync<List<StatusDto>>("/api/hatch/statuses", ct) ?? [];

    /// <summary>One issue, whole: the brief, its edges and where it is.</summary>
    public Task<IssueDto?> IssueAsync(string key, CancellationToken ct) =>
        Client.GetAsync<IssueDto>($"/api/hatch/issues/{key}", ct);

    public async Task<IReadOnlyList<CommentDto>> CommentsAsync(string key, CancellationToken ct) =>
        await Client.GetAsync<List<CommentDto>>($"/api/hatch/issues/{key}/comments", ct) ?? [];

    /// <summary>Into a column. The board decides the rank; nothing here has an opinion about it.</summary>
    public Task<IssueDto?> MoveAsync(string key, int statusId, CancellationToken ct) =>
        Client.PostAsync<IssueDto>($"/api/hatch/issues/{key}/move", new IssueMoveRequest(statusId, null, null), ct);

    /// <summary>One field at a time, which is all the CLI ever edits.</summary>
    public Task<IssueDto?> PatchAsync(string key, IssuePatchRequest patch, CancellationToken ct) =>
        Client.WriteAsync<IssueDto>(HttpMethod.Patch, $"/api/hatch/issues/{key}", patch, ct);

    /// <summary><paramref name="key"/> waits on <paramref name="dependsOnKey"/>.</summary>
    public Task<IssueDto?> DependAsync(string key, string dependsOnKey, CancellationToken ct) =>
        Client.PostAsync<IssueDto>(
            $"/api/hatch/issues/{key}/dependencies", new IssueDependencyRequest(dependsOnKey), ct);

    /// <summary>...no longer.</summary>
    public Task<IssueDto?> UndependAsync(string key, string dependsOnKey, CancellationToken ct) =>
        Client.WriteAsync<IssueDto>(
            HttpMethod.Delete, $"/api/hatch/issues/{key}/dependencies/{dependsOnKey}", null, ct);

    /// <summary>An answer to a question, which is a comment that names the one it answers.</summary>
    public Task<CommentDto?> AnswerAsync(string key, long questionId, string body, CancellationToken ct) =>
        Client.PostAsync<CommentDto>(
            $"/api/hatch/issues/{key}/comments",
            new CommentCreateRequest(body, Kind: "answer", AnswersId: questionId),
            ct);
}

/// <summary>The reasons a pass folded past what it folded past, counted rather than listed.</summary>
/// <remarks>
/// A column of two hundred cards folded for four reasons is four facts, and the
/// list of two hundred is not one of them.
/// </remarks>
public static class Digest
{
    /// <summary>The folded rows, grouped by reason, commonest first and then alphabetically.</summary>
    public static IReadOnlyList<string> Of(IReadOnlyList<QueueEntryDto> queue)
    {
        var folded = queue.Where(q => q.Blocked is { Length: > 0 }).ToList();
        if (folded.Count == 0) return [];

        var groups = folded
            .GroupBy(q => q.Blocked!)
            .Select(g => (Count: g.Count(), Why: g.Key))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Why, StringComparer.Ordinal)
            .ToList();

        var width = groups.Max(g => g.Count.ToString().Length);
        return groups.Select(g => $"hatch:     {g.Count.ToString().PadLeft(width)}  {g.Why}").ToList();
    }
}
