using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// What each ticket actually cost: one row per agent session, written by the
/// dispatcher at the end of every unattended increment and read back on the
/// issue page.
///
/// The dispatcher is the only thing in the system that knows which ticket a
/// session's spend was for - account-wide utilization is honest but anonymous -
/// so this is where that knowledge is kept. See
/// <see cref="EfHatchWorkLogEntry"/> for what a row holds and why.
/// </summary>
/// <remarks>
/// Nothing here writes an <see cref="EfHatchIssueEvent"/>, and that is
/// deliberate. The trail is a record of what people and agents <em>decided</em>;
/// a work log row is the meter reading, and doubling it into the trail would put
/// a line on every ticket's history saying nothing the row does not.
/// </remarks>
[ApiController]
[Route("api/hatch/issues/{key}/work-log")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class IssueWorkLogController(HatchContext db, TimeProvider time, ICallerIdentity caller) : ControllerBase
{
    /// <summary>
    /// Record what one session cost on this issue.
    /// </summary>
    /// <remarks>
    /// Idempotent by <c>(issue, session)</c>: the row is read first and updated
    /// in place when it is there, inserted when it is not, and the answer is a
    /// <c>200</c> either way rather than a <c>409</c>. The unique index is the
    /// backstop and not the mechanism - EF's in-memory provider does not enforce
    /// one, and the caller is a shell script at the end of a run that can be
    /// re-run.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<WorkLogEntryDto>> PostEntry(
        string key, WorkLogEntryRequest request, CancellationToken ct)
    {
        // A key, not merely an administrator - see NotAKey.
        if (await NotAKey(ct) is { } refusal) return refusal;

        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issueId = await db.Issues.WithKey(projectKey, number)
            .Select(i => (long?)i.Id)
            .FirstOrDefaultAsync(ct);
        if (issueId is null) return NotFound();

        var sessionId = request.SessionId?.Trim();
        if (string.IsNullOrEmpty(sessionId))
            return BadRequest("a work log entry needs the session it is about");
        if (sessionId.Length > EfHatchWorkLogEntry.MaxSessionIdLength)
            return BadRequest($"a session id is at most {EfHatchWorkLogEntry.MaxSessionIdLength} characters");

        // Clipped rather than refused, and that is the rule for both texts. The
        // hundred words a session is asked for are an instruction to the session,
        // not a validation on the row: refusing an over-long summary would lose
        // an evening's spend to a style note.
        var title = Clip(request.Title, EfHatchWorkLogEntry.MaxTitleLength);
        var summary = Clip(request.Summary, EfHatchWorkLogEntry.MaxSummaryLength);

        var models = request.Models is { Count: > 0 } ? request.Models : [];

        var entry = await db.WorkLog
            .FirstOrDefaultAsync(w => w.IssueId == issueId && w.SessionId == sessionId, ct);

        if (entry is null)
        {
            entry = new EfHatchWorkLogEntry
            {
                IssueId = issueId.Value,
                SessionId = sessionId,
                StartedAt = request.StartedAt,
                EndedAt = request.EndedAt,
                DurationMs = request.DurationMs,
                CreatedAt = time.GetUtcNow(),
            };
            db.WorkLog.Add(entry);
        }
        else
        {
            entry.StartedAt = request.StartedAt;
            entry.EndedAt = request.EndedAt;
            entry.DurationMs = request.DurationMs;
        }

        entry.Title = title;
        entry.Summary = summary;

        // Not the caller's to send: a session that said nothing about itself
        // still gets its row, and whether it described itself is a fact the
        // server reads off what actually arrived.
        entry.Described = title is not null || summary is not null;

        entry.IsError = request.IsError;
        entry.Turns = request.Turns;
        entry.CostUsd = request.CostUsd;

        // The four counts are the sum of the breakdown and are computed here
        // rather than accepted, so a row's total and its detail cannot come to
        // disagree - "the total equals the breakdown" is true by construction
        // instead of by a check somebody remembered.
        entry.InputTokens = models.Sum(m => m.InputTokens);
        entry.OutputTokens = models.Sum(m => m.OutputTokens);
        entry.CacheCreationTokens = models.Sum(m => m.CacheCreationTokens);
        entry.CacheReadTokens = models.Sum(m => m.CacheReadTokens);
        entry.ModelUsage = WriteModels(models);

        await db.SaveChangesAsync(ct);

        return Project(entry);
    }

    /// <summary>
    /// This issue's sessions and what its whole subtree has cost.
    /// </summary>
    /// <remarks>
    /// Entries are the issue's own; totals cover everything beneath it too. See
    /// <see cref="WorkLogDto"/> for why the two differ and
    /// <see cref="WorkLogRollup"/> for why the addition is plain addition.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<WorkLogDto>> GetWorkLog(string key, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issue = await db.Issues.AsNoTracking().WithKey(projectKey, number)
            .Select(i => new { i.Id, i.Number, ProjectKey = i.Project!.Key })
            .FirstOrDefaultAsync(ct);
        if (issue is null) return NotFound();

        var entries = await db.WorkLog.AsNoTracking()
            .Where(w => w.IssueId == issue.Id)
            // Newest first: the question a work log gets asked is "what did this
            // cost last night", not "how did it begin".
            .OrderByDescending(w => w.EndedAt)
            .ThenByDescending(w => w.Id)
            .ToListAsync(ct);

        return new WorkLogDto(
            IssueKey.Format(issue.ProjectKey, issue.Number),
            await WorkLogRollup.TotalsAsync(db, issue.Id, includeDescendants: true, ct),
            await WorkLogRollup.TotalsAsync(db, issue.Id, includeDescendants: false, ct),
            entries.Select(Project).ToList());
    }

    /// <summary>
    /// <c>403</c> when the caller is a person rather than a program, or null
    /// when it is a key.
    /// </summary>
    /// <remarks>
    /// <see cref="RequireAdminAttribute"/> accepts both an administrator and a
    /// scoped key; this route accepts only the key, because the only honest
    /// writer of a meter reading is the thing that read the meter. There is no
    /// control for writing an entry anywhere in the browser and there is not
    /// meant to be one.
    ///
    /// Checked in the action rather than expressed in the attribute on purpose:
    /// <c>AdminGate</c> is dormant wherever <c>Auth:EnforceAdmin</c> is off,
    /// which is all of local development, and a guarantee that evaporates under
    /// a switch is not a guarantee.
    /// </remarks>
    private async Task<ObjectResult?> NotAKey(CancellationToken ct) =>
        await caller.ApiKeyAsync(ct) is null
            ? new ObjectResult("a work log entry is written by the dispatcher, with an API key")
            {
                StatusCode = StatusCodes.Status403Forbidden,
            }
            : null;

    private static WorkLogEntryDto Project(EfHatchWorkLogEntry w) => new(
        w.Id,
        w.SessionId,
        w.StartedAt,
        w.EndedAt,
        w.DurationMs,
        w.Title,
        w.Summary,
        w.Described,
        w.IsError,
        w.Turns,
        w.CostUsd,
        w.InputTokens,
        w.OutputTokens,
        w.CacheCreationTokens,
        w.CacheReadTokens,
        w.InputTokens + w.OutputTokens + w.CacheCreationTokens + w.CacheReadTokens,
        ReadModels(w.ModelUsage));

    /// <summary>What goes in the column, or null when the run reported no models at all.</summary>
    private static string? WriteModels(IReadOnlyList<WorkLogModelUseDto> models) =>
        models.Count > 0 ? JsonSerializer.Serialize(models, Json) : null;

    /// <summary>
    /// The stored breakdown, or nothing at all. A column that will not parse -
    /// hand-edited, or written by a shape this build does not know - reads as
    /// empty rather than throwing the whole log away, the way
    /// <c>IssueThreadController.Payload</c> does.
    /// </summary>
    private static IReadOnlyList<WorkLogModelUseDto> ReadModels(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<WorkLogModelUseDto>>(stored, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Whitespace off, over-long text cut - or null when nothing was said.</summary>
    private static string? Clip(string? text, int max)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    /// <summary>camelCase, matching the wire - see <see cref="Questions"/>.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
