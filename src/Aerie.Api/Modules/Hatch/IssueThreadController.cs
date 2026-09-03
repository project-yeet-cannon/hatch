using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// What has been said about an issue and what has happened to it - the two
/// lists at the bottom of the detail page.
///
/// A sibling of <see cref="IssuesController"/> rather than four more actions on
/// it: the issue controller is already the module's longest file, and these
/// three verbs share nothing with it but the key lookup.
/// </summary>
[ApiController]
[Route("api/hatch/issues/{key}")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class IssueThreadController(HatchContext db, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    [HttpGet("comments")]
    public async Task<ActionResult<IReadOnlyList<CommentDto>>> GetComments(string key, CancellationToken ct)
    {
        if (await IssueIdAsync(key, ct) is not { } issueId) return NotFound();

        var comments = await db.Comments.AsNoTracking()
            .Where(c => c.IssueId == issueId)
            // Oldest first: a comment thread is read downwards.
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => new CommentDto(c.Id, c.Author, c.Body, c.CreatedAt))
            .ToListAsync(ct);

        return comments;
    }

    [HttpPost("comments")]
    public async Task<ActionResult<CommentDto>> AddComment(string key, CommentCreateRequest request, CancellationToken ct)
    {
        if (await IssueIdAsync(key, ct) is not { } issueId) return NotFound();

        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return BadRequest("a comment needs something in it");
        if (body.Length > EfHatchComment.MaxBodyLength)
            return BadRequest($"a comment is at most {EfHatchComment.MaxBodyLength} characters");

        var actor = await caller.ActorNameAsync(ct);
        var now = time.GetUtcNow();

        var comment = new EfHatchComment { IssueId = issueId, Author = actor, Body = body, CreatedAt = now };
        db.Comments.Add(comment);

        // The event carries no copy of the body - the comment row is the record,
        // and duplicating it here would mean an edit could make the two disagree.
        db.IssueEvents.Add(new EfHatchIssueEvent
        {
            IssueId = issueId,
            Actor = actor,
            Kind = EfHatchIssueEvent.Commented,
            At = now,
        });

        await db.SaveChangesAsync(ct);

        return new CommentDto(comment.Id, comment.Author, comment.Body, comment.CreatedAt);
    }

    /// <summary>
    /// The audit trail, newest first - the order it is read in, because the
    /// question is almost always "what just happened to this".
    /// </summary>
    [HttpGet("events")]
    public async Task<ActionResult<IReadOnlyList<IssueEventDto>>> GetEvents(string key, CancellationToken ct)
    {
        if (await IssueIdAsync(key, ct) is not { } issueId) return NotFound();

        var events = await db.IssueEvents.AsNoTracking()
            .Where(e => e.IssueId == issueId)
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .ToListAsync(ct);

        return events.Select(e => new IssueEventDto(e.Id, e.Actor, e.Kind, Payload(e.Payload), e.At)).ToList();
    }

    /// <summary>
    /// The stored payload as JSON rather than as a string of JSON, so a client
    /// reads it without a second parse. A row that will not parse - hand-edited,
    /// or written by a shape this build does not know - reads as absent rather
    /// than throwing the whole trail away.
    /// </summary>
    private static JsonElement? Payload(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        try
        {
            return JsonDocument.Parse(stored).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<long?> IssueIdAsync(string key, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return null;

        return await db.Issues.WithKey(projectKey, number).Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
    }
}
