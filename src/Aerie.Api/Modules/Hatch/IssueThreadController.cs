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
            .Select(c => new CommentDto(c.Id, c.Author, c.Body, c.Kind, c.AnswersId, c.CreatedAt))
            .ToListAsync(ct);

        return comments;
    }

    /// <summary>
    /// Say something on the issue - a note, a question, or the answer to one.
    /// </summary>
    /// <remarks>
    /// Nothing here refuses an API key an answer, and that is a deliberate gap
    /// rather than an oversight. A key is the operator's own credential - it is
    /// what <c>hatch.sh answer</c> types with - and a spawned agent inherits the
    /// same one from the environment it was started in, so the server cannot
    /// tell the person from the process it dispatched. Pretending otherwise
    /// would be a check that reads like a guarantee and is not one.
    ///
    /// What actually holds the loop shut is a step further out: an open question
    /// blocks <see cref="WorkController"/> from dispatching at all, and the
    /// dispatch is a command the operator types. An agent that answered its own
    /// question would be a session that had already been told to stop.
    /// </remarks>
    [HttpPost("comments")]
    public async Task<ActionResult<CommentDto>> AddComment(string key, CommentCreateRequest request, CancellationToken ct)
    {
        if (await IssueIdAsync(key, ct) is not { } issueId) return NotFound();

        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return BadRequest("a comment needs something in it");
        if (body.Length > EfHatchComment.MaxBodyLength)
            return BadRequest($"a comment is at most {EfHatchComment.MaxBodyLength} characters");

        var kind = request.Kind?.Trim() ?? EfHatchComment.Note;
        if (!EfHatchComment.IsValidKind(kind))
            return BadRequest($"\"{kind}\" is not a kind of comment - it is \"{EfHatchComment.Question}\", \"{EfHatchComment.Answer}\", or nothing at all");

        // An answer names its question; nothing else may. Checked rather than
        // ignored, because a client that sent both a note and an answersId has
        // misunderstood something, and silently dropping half its request is
        // how it stays misunderstood.
        if (kind != EfHatchComment.Answer && request.AnswersId is not null)
            return BadRequest($"only an \"{EfHatchComment.Answer}\" answers a question");

        if (kind == EfHatchComment.Answer)
        {
            if (request.AnswersId is not { } answersId)
                return BadRequest("an answer needs the id of the question it answers");

            // Same issue, and actually a question. A cross-issue link would put
            // an answer on a thread nobody reading the question can see.
            var question = await db.Comments.AsNoTracking()
                .Where(c => c.Id == answersId && c.IssueId == issueId && c.Kind == EfHatchComment.Question)
                .Select(c => (long?)c.Id)
                .FirstOrDefaultAsync(ct);

            if (question is null)
                return BadRequest($"comment {answersId} is not an open question on {key}");
        }

        var actor = await caller.ActorNameAsync(ct);
        var now = time.GetUtcNow();

        var comment = new EfHatchComment
        {
            IssueId = issueId,
            Author = actor,
            Body = body,
            Kind = kind,
            AnswersId = request.AnswersId,
            CreatedAt = now,
        };
        db.Comments.Add(comment);

        // The event carries no copy of the body - the comment row is the record,
        // and duplicating it here would mean an edit could make the two disagree.
        // An answer carries the question's id, because "when did this stop
        // waiting on somebody" is the question the trail gets asked.
        db.IssueEvents.Add(new EfHatchIssueEvent
        {
            IssueId = issueId,
            Actor = actor,
            Kind = kind switch
            {
                EfHatchComment.Question => EfHatchIssueEvent.Asked,
                EfHatchComment.Answer => EfHatchIssueEvent.Answered,
                _ => EfHatchIssueEvent.Commented,
            },
            Payload = kind == EfHatchComment.Answer
                ? JsonSerializer.Serialize(new { questionId = request.AnswersId })
                : null,
            At = now,
        });

        await db.SaveChangesAsync(ct);

        return new CommentDto(comment.Id, comment.Author, comment.Body, comment.Kind, comment.AnswersId, comment.CreatedAt);
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
