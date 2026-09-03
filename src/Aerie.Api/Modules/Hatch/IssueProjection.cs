using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// One issue, rendered whole for the wire.
///
/// A static rather than a method on <see cref="IssuesController"/> because two
/// controllers now hand out an <see cref="IssueDto"/> - the issue endpoints and
/// the work endpoints - and an issue that reads one way through one route and
/// another way through the other is the kind of divergence nobody notices until
/// a client trusts the wrong one.
/// </summary>
public static class IssueProjection
{
    public static async Task<IssueDto> ToDtoAsync(HatchContext db, EfHatchIssue issue, CancellationToken ct)
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
            issue.ParentId is { } parentId ? await KeyOfAsync(db, parentId, ct) : null,
            children.Select(c => IssueKey.Format(c.Key, c.Number)).ToList(),
            IssueMoment.Format(issue.ReadyAt, issue.ReadyAtHasTime),
            IssueMoment.Format(issue.DueAt, issue.DueAtHasTime),
            issue.CreatedBy,
            issue.CreatedAt,
            issue.UpdatedAt);
    }

    /// <summary>The display key of an issue named by id, or null if it has gone.</summary>
    public static async Task<string?> KeyOfAsync(HatchContext db, long issueId, CancellationToken ct)
    {
        var found = await db.Issues.Where(i => i.Id == issueId)
            .Select(i => new { i.Project!.Key, i.Number })
            .FirstOrDefaultAsync(ct);

        return found is null ? null : IssueKey.Format(found.Key, found.Number);
    }
}
