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
    /// <summary>
    /// One issue. The single-issue case of <see cref="ToDtosAsync"/> rather
    /// than a second projection beside it: the endpoint that hands out one
    /// issue and the scan that hands out a hundred should not be able to
    /// disagree about what an issue looks like.
    /// </summary>
    public static async Task<IssueDto> ToDtoAsync(HatchContext db, EfHatchIssue issue, CancellationToken ct) =>
        (await ToDtosAsync(db, [issue], ct))[issue.Id];

    /// <summary>
    /// A batch of issues, in a fixed number of queries rather than a fixed
    /// number *per issue*.
    /// </summary>
    /// <remarks>
    /// The three things an <see cref="IssueDto"/> needs beyond its own row -
    /// its project's key, its parent's key, and its children's keys - are each
    /// one query for the whole batch. That is what makes a whole-board read
    /// affordable: <see cref="WorkController"/>'s scan projects every issue the
    /// dispatcher would consider, and a per-row parent lookup would turn one
    /// answer into a few hundred round trips.
    /// </remarks>
    public static async Task<Dictionary<long, IssueDto>> ToDtosAsync(
        HatchContext db, IReadOnlyList<EfHatchIssue> issues, CancellationToken ct)
    {
        if (issues.Count == 0) return [];

        var ids = issues.Select(i => i.Id).Distinct().ToList();

        var projectIds = issues.Select(i => i.ProjectId).Distinct().ToList();
        var projectKeys = await db.Projects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Key, ct);

        // The parents of the batch, which are not necessarily in the batch -
        // a story scanned in one column hangs under an epic sitting in another.
        var parentIds = issues.Select(i => i.ParentId).OfType<long>().Distinct().ToList();
        var parentKeys = await db.Issues.AsNoTracking()
            .Where(i => parentIds.Contains(i.Id))
            .Select(i => new { i.Id, ProjectKey = i.Project!.Key, i.Number })
            .ToDictionaryAsync(r => r.Id, r => IssueKey.Format(r.ProjectKey, r.Number), ct);

        var childRows = await db.Issues.AsNoTracking()
            .Where(i => i.ParentId != null && ids.Contains(i.ParentId!.Value))
            .OrderBy(i => i.Number)
            .Select(i => new { ParentId = i.ParentId!.Value, ProjectKey = i.Project!.Key, i.Number })
            .ToListAsync(ct);

        var childKeys = childRows
            .GroupBy(r => r.ParentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g
                .Select(r => IssueKey.Format(r.ProjectKey, r.Number)).ToList());

        return issues.ToDictionary(issue => issue.Id, issue =>
        {
            var projectKey = issue.Project?.Key ?? projectKeys[issue.ProjectId];

            return new IssueDto(
                IssueKey.Format(projectKey, issue.Number),
                issue.ProjectId,
                projectKey,
                issue.Type,
                issue.Title,
                issue.Description,
                issue.StatusId,
                issue.Rank,
                issue.ParentId is { } parentId && parentKeys.TryGetValue(parentId, out var found) ? found : null,
                childKeys.TryGetValue(issue.Id, out var children) ? children : [],
                IssueMoment.Format(issue.ReadyAt, issue.ReadyAtHasTime),
                IssueMoment.Format(issue.DueAt, issue.DueAtHasTime),
                issue.CreatedBy,
                issue.CreatedAt,
                issue.UpdatedAt);
        });
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
