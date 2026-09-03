using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// Issues: the create, the read, the edit, the delete, and the drag.
///
/// Two things here are worth reading before changing anything. Every mutation
/// writes an <see cref="EfHatchIssueEvent"/>, from the first release, because
/// an event log is the one feature that cannot be added retroactively - turned
/// on in March it answers nothing about February. And the create path mints its
/// own number under optimistic concurrency rather than reaching for a sequence,
/// so it stays testable in memory and its failure mode is a retry rather than a
/// duplicated key (docs/plans/pjm.md, "Issue numbering").
/// </summary>
[ApiController]
[Route("api/hatch/issues")]
[RequireAdmin]
public class IssuesController(HatchContext db, RankService ranks, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// How many times a create will re-read the project and try again. Five is
    /// far past what two humans and an agent can produce; the point of a bound
    /// is that a pathological loop ends in a 409 rather than in a spinning
    /// request.
    /// </summary>
    private const int MintAttempts = 5;

    // ---- Reading ----

    [HttpGet("{key}")]
    public async Task<ActionResult<IssueDto>> GetIssue(string key, CancellationToken ct)
    {
        var issue = await LoadAsync(key, ct);
        return issue is null ? NotFound() : await ToDtoAsync(issue, ct);
    }

    // ---- Creating ----

    /// <summary>
    /// Files an issue. It lands in the leftmost column and at the bottom of it,
    /// both decided here - so no client has to know what the inbox is called
    /// this week, and a script that files a bug is the same three fields as the
    /// dialog.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<IssueDto>> CreateIssue(IssueCreateRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim();
        if (Invalid(title, request.Description, request.Type) is { } invalid) return BadRequest(invalid);

        var status = await db.Statuses.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).FirstOrDefaultAsync(ct);
        if (status is null) return Conflict("this board has no columns to put an issue in");

        var actor = await caller.ActorAsync(ct);
        var now = time.GetUtcNow();

        for (var attempt = 1; ; attempt++)
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == request.ProjectId, ct);
            if (project is null) return NotFound();

            var parent = await ResolveParentAsync(request.ParentKey, project.Id, request.Type, null, ct);
            if (parent.Error is { } error) return BadRequest(error);

            var issue = new EfHatchIssue
            {
                ProjectId = project.Id,
                Number = project.NextIssueNumber,
                Type = request.Type,
                Title = title!,
                Description = request.Description ?? "",
                StatusId = status.Id,
                ParentId = parent.Issue?.Id,
                Rank = await ranks.BottomAsync(status.Id, ct),
                CreatedBy = actor,
                CreatedAt = now,
                UpdatedAt = now,
            };

            // The mint and the issue in one SaveChanges. NextIssueNumber is a
            // concurrency token, so a second request that read the same number
            // fails here rather than writing a second AER-12 - and the unique
            // index on (ProjectId, Number) is the backstop under that.
            project.NextIssueNumber++;
            issue.Events.Add(Event(actor, EfHatchIssueEvent.Created, new { type = issue.Type, title = issue.Title }, now));
            db.Issues.Add(issue);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (attempt < MintAttempts)
            {
                // Someone took the number between our read and our write. Drop
                // everything this attempt staged - including the failed insert,
                // which would otherwise be retried alongside the next one - and
                // read the project again.
                db.ChangeTracker.Clear();
                continue;
            }
            catch (DbUpdateException)
            {
                return Conflict("could not mint an issue number - try again");
            }

            return CreatedAtAction(nameof(GetIssue), new { key = await KeyOfAsync(issue, ct) }, await ToDtoAsync(issue, ct));
        }
    }

    // ---- Editing ----

    /// <summary>
    /// Changes whichever fields the body names - null means "leave this alone",
    /// with the one exception the DTO spells out: an empty
    /// <see cref="IssuePatchRequest.ParentKey"/> clears the parent.
    ///
    /// One event per changed field, and nothing at all for a field re-sent
    /// unchanged. An audit trail that logs a no-op edit is one nobody reads.
    /// </summary>
    [HttpPatch("{key}")]
    public async Task<ActionResult<IssueDto>> PatchIssue(string key, IssuePatchRequest request, CancellationToken ct)
    {
        var issue = await LoadAsync(key, ct);
        if (issue is null) return NotFound();

        if (Invalid(request.Title?.Trim(), request.Description, request.Type, required: false) is { } invalid)
            return BadRequest(invalid);

        var actor = await caller.ActorAsync(ct);
        var now = time.GetUtcNow();
        var events = new List<EfHatchIssueEvent>();

        if (request.Title?.Trim() is { Length: > 0 } title && title != issue.Title)
        {
            events.Add(Event(actor, EfHatchIssueEvent.Retitled, new { from = issue.Title, to = title }, now));
            issue.Title = title;
        }

        if (request.Description is { } description && description != issue.Description)
        {
            events.Add(Event(actor, EfHatchIssueEvent.Redescribed, new { from = issue.Description, to = description }, now));
            issue.Description = description;
        }

        if (request.Type is { } type && type != issue.Type)
        {
            events.Add(Event(actor, EfHatchIssueEvent.Retyped, new { from = issue.Type, to = type }, now));
            issue.Type = type;
        }

        if (request.StatusId is { } statusId && statusId != issue.StatusId)
        {
            var status = await db.Statuses.FirstOrDefaultAsync(s => s.Id == statusId, ct);
            if (status is null) return BadRequest($"there is no column {statusId}");

            var from = await db.Statuses.Where(s => s.Id == issue.StatusId).Select(s => s.Name).FirstOrDefaultAsync(ct);
            events.Add(Event(actor, EfHatchIssueEvent.StatusChanged, new { from, to = status.Name }, now));

            issue.StatusId = statusId;
            // A column change through PATCH has no neighbours to sit between,
            // so the card goes to the bottom of the new column. The board sends
            // its drops to `move`, which does have them.
            issue.Rank = await ranks.BottomAsync(statusId, ct);
        }

        // Present-but-empty is the clear; absent is no opinion. See IssuePatchRequest.
        if (request.ParentKey is not null)
        {
            var parent = await ResolveParentAsync(request.ParentKey, issue.ProjectId, request.Type ?? issue.Type, issue.Id, ct);
            if (parent.Error is { } error) return BadRequest(error);

            if (parent.Issue?.Id != issue.ParentId)
            {
                var from = issue.ParentId is null ? null : await KeyOfAsync(issue.ParentId.Value, ct);
                var to = parent.Issue is null ? null : await KeyOfAsync(parent.Issue, ct);
                events.Add(Event(actor, EfHatchIssueEvent.ParentChanged, new { from, to }, now));
                issue.ParentId = parent.Issue?.Id;
            }
        }

        if (events.Count > 0)
        {
            foreach (var e in events) issue.Events.Add(e);
            issue.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return await ToDtoAsync(issue, ct);
    }

    /// <summary>
    /// Removes an issue, its comments, and its events. The accepted MVP gap,
    /// written down in the plan: a deleted issue takes its audit trail with it.
    ///
    /// Its children are outdented rather than deleted. Losing a parent is an
    /// outdent; deleting an epic should not quietly take eleven stories off the
    /// board.
    /// </summary>
    [HttpDelete("{key}")]
    public async Task<IActionResult> DeleteIssue(string key, CancellationToken ct)
    {
        var issue = await LoadAsync(key, ct);
        if (issue is null) return NotFound();

        var children = await db.Issues.Where(i => i.ParentId == issue.Id).ToListAsync(ct);
        foreach (var child in children) child.ParentId = null;

        db.Issues.Remove(issue);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- The drag ----

    /// <summary>
    /// Where a card landed. The client names the neighbours it can see and
    /// never a rank - the server computes the number, which is what keeps the
    /// board, a script, and Claude all moving a card the same way.
    /// </summary>
    /// <remarks>
    /// A move within a column writes no event. Tidying a column is board
    /// hygiene rather than work, and logging it would bury the status changes
    /// that matter under a hundred lines of dragging.
    /// </remarks>
    [HttpPost("{key}/move")]
    public async Task<ActionResult<IssueDto>> MoveIssue(string key, IssueMoveRequest request, CancellationToken ct)
    {
        var issue = await LoadAsync(key, ct);
        if (issue is null) return NotFound();

        var status = await db.Statuses.FirstOrDefaultAsync(s => s.Id == request.StatusId, ct);
        if (status is null) return BadRequest($"there is no column {request.StatusId}");

        var after = await NeighbourIdAsync(request.AfterKey, ct);
        var before = await NeighbourIdAsync(request.BeforeKey, ct);

        var changedColumn = issue.StatusId != status.Id;
        if (changedColumn)
        {
            var actor = await caller.ActorAsync(ct);
            var now = time.GetUtcNow();
            var from = await db.Statuses.Where(s => s.Id == issue.StatusId).Select(s => s.Name).FirstOrDefaultAsync(ct);
            issue.Events.Add(Event(actor, EfHatchIssueEvent.StatusChanged, new { from, to = status.Name }, now));
            issue.UpdatedAt = now;
            issue.StatusId = status.Id;
        }

        // The rank is computed after the column is set, and both are saved in
        // one call - so a renumbered column and the card that caused it can
        // never land separately.
        issue.Rank = await ranks.PlaceAsync(status.Id, issue.Id, after, before, ct);
        await db.SaveChangesAsync(ct);

        return await ToDtoAsync(issue, ct);
    }

    // ---- Loading ----

    /// <summary>
    /// The issue a display key names, tracked. Any key that does not resolve -
    /// malformed, an unknown project, a number nobody minted - is the same 404,
    /// because there is nothing useful to tell apart between them.
    /// </summary>
    private async Task<EfHatchIssue?> LoadAsync(string key, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return null;

        return await db.Issues.Include(i => i.Project).WithKey(projectKey, number).FirstOrDefaultAsync(ct);
    }

    /// <summary>A neighbour named by the client, or null when it named none - or one that has since gone.</summary>
    private async Task<long?> NeighbourIdAsync(string? key, CancellationToken ct)
    {
        if (key is null || !IssueKey.TryParse(key, out var projectKey, out var number)) return null;

        return await db.Issues.WithKey(projectKey, number).Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
    }

    // ---- Parenting ----

    /// <summary>
    /// The parent a key names, checked against every rule that makes a
    /// hierarchy a hierarchy: it exists, it is in the same project, the type
    /// pairing is legal, and it is not below the issue being parented.
    /// </summary>
    /// <param name="parentKey">Null for "no opinion", empty for "no parent".</param>
    /// <param name="selfId">The issue being parented, when it already exists - what the cycle walk is looking for.</param>
    private async Task<(EfHatchIssue? Issue, string? Error)> ResolveParentAsync(
        string? parentKey, int projectId, string type, long? selfId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parentKey)) return (null, null);
        if (!IssueKey.TryParse(parentKey, out var projectKeyPart, out var number))
            return (null, $"\"{parentKey}\" is not an issue key");

        var parent = await db.Issues.Include(i => i.Project)
            .WithKey(projectKeyPart, number).FirstOrDefaultAsync(ct);
        if (parent is null) return (null, $"there is no {parentKey}");

        if (parent.ProjectId != projectId)
            return (null, $"{parentKey} is in another project - an issue and its parent share one");

        if (parent.Id == selfId) return (null, "an issue cannot be its own parent");

        if (EfHatchIssue.LegalParentTypes.TryGetValue(type, out var legal) && !legal.Contains(parent.Type))
            return (null,
                $"{Article(type)} {type} hangs under {string.Join(" or ", legal.Select(t => $"{Article(t)} {t}"))}, "
                + $"not {Article(parent.Type)} {parent.Type}");

        // Walk up from the proposed parent. Reaching the issue being parented
        // means the link would close a loop - which is not merely untidy: the
        // detail page follows parents, and a loop is a page that never finishes
        // rendering.
        if (selfId is { } self)
        {
            var seen = new HashSet<long>();
            var ancestor = parent.ParentId;
            while (ancestor is { } id && seen.Add(id))
            {
                if (id == self) return (null, $"{parentKey} is already below this issue");
                ancestor = await db.Issues.Where(i => i.Id == id).Select(i => i.ParentId).FirstOrDefaultAsync(ct);
            }
        }

        return (parent, null);
    }

    // ---- Mapping ----

    /// <summary>
    /// "an epic", "a story". These sentences are read on screen by the person
    /// who just tried the thing, and "a epic" reads as a bug in everything
    /// around it.
    /// </summary>
    private static string Article(string noun) =>
        noun.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an" : "a";

    private static EfHatchIssueEvent Event(string actor, string kind, object? payload, DateTimeOffset at) => new()
    {
        Actor = actor,
        Kind = kind,
        Payload = payload is null ? null : JsonSerializer.Serialize(payload),
        At = at,
    };

    private async Task<string> KeyOfAsync(EfHatchIssue issue, CancellationToken ct) =>
        IssueKey.Format(
            issue.Project?.Key ?? await db.Projects.Where(p => p.Id == issue.ProjectId).Select(p => p.Key).SingleAsync(ct),
            issue.Number);

    private async Task<string?> KeyOfAsync(long issueId, CancellationToken ct)
    {
        var found = await db.Issues.Where(i => i.Id == issueId)
            .Select(i => new { i.Project!.Key, i.Number })
            .FirstOrDefaultAsync(ct);

        return found is null ? null : IssueKey.Format(found.Key, found.Number);
    }

    private async Task<IssueDto> ToDtoAsync(EfHatchIssue issue, CancellationToken ct)
    {
        var projectKey = issue.Project?.Key
            ?? await db.Projects.Where(p => p.Id == issue.ProjectId).Select(p => p.Key).SingleAsync(ct);

        var children = await db.Issues.Where(i => i.ParentId == issue.Id)
            .OrderBy(i => i.Number)
            .Select(i => new { i.Project!.Key, i.Number })
            .ToListAsync(ct);

        return new IssueDto(
            IssueKey.Format(projectKey, issue.Number),
            issue.ProjectId,
            projectKey,
            issue.Type,
            issue.Title,
            issue.Description,
            issue.StatusId,
            issue.Rank,
            issue.ParentId is { } parentId ? await KeyOfAsync(parentId, ct) : null,
            children.Select(c => IssueKey.Format(c.Key, c.Number)).ToList(),
            issue.CreatedBy,
            issue.CreatedAt,
            issue.UpdatedAt);
    }

    /// <summary>
    /// The field rules, shared by create and patch. <paramref name="required"/>
    /// is what separates them: a create needs a title and a type, a patch may
    /// mention neither.
    /// </summary>
    private static string? Invalid(string? title, string? description, string? type, bool required = true)
    {
        if (required && string.IsNullOrEmpty(title)) return "an issue needs a title";
        if (title is { Length: > EfHatchIssue.MaxTitleLength })
            return $"a title is at most {EfHatchIssue.MaxTitleLength} characters";
        if (description is { Length: > EfHatchIssue.MaxDescriptionLength })
            return $"a description is at most {EfHatchIssue.MaxDescriptionLength} characters";
        if (required || type is not null)
            if (!EfHatchIssue.IsValidType(type))
                return $"an issue is one of {string.Join(", ", EfHatchIssue.Types)} - not \"{type}\"";
        return null;
    }
}
