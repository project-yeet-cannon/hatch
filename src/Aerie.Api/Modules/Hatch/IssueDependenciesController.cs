using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// What an issue waits on: the two verbs that add and remove an edge.
///
/// Writing one is open to a key, unlike a playbook or a per-issue override
/// (<see cref="IssuePlaybookController"/>). The difference is what the write
/// says: an override raises what an agent is spent, and an edge states a fact
/// about the work - that these two things must land in order. A planning
/// session that has just filed five stories is exactly who should chain them,
/// so this rides the ordinary scope-accepting guard the rest of the module
/// carries.
/// </summary>
/// <remarks>
/// There is no <c>GET</c> here. Both lists ride <see cref="IssueDto"/>, which
/// is where the board, the page and the shell need them anyway, and a second
/// route serving the same two arrays would be a second thing to keep in step.
/// Both verbs answer with the whole issue for the same reason: the page
/// repaints from one response instead of composing the new state itself.
/// </remarks>
[ApiController]
[Route("api/hatch/issues/{key}/dependencies")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class IssueDependenciesController(
    HatchContext db, IActorDirectory actors, IssueClaims claims, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Makes this issue wait on another. Adding an edge that is already there
    /// writes nothing, logs nothing and moves no <c>UpdatedAt</c> - re-applying
    /// an edit is safe everywhere else in Hatch and is safe here.
    /// </summary>
    /// <remarks>
    /// Every refusal is judged before anything is written, so a request that is
    /// refused leaves the table exactly as it was. They are ordered by how
    /// cheaply they can be answered and how fundamental they are: a key that
    /// names nothing, then the issue itself, then the tree, then the chain.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<IssueDto>> AddDependency(
        string key, IssueDependencyRequest request, CancellationToken ct)
    {
        if (await LoadAsync(key, ct) is not { } issue) return NotFound();

        var dependsOnKey = request.DependsOnKey?.Trim() ?? "";
        if (!IssueKey.TryParse(dependsOnKey, out var projectKey, out var number))
            return BadRequest($"\"{dependsOnKey}\" is not an issue key");

        var blocker = await db.Issues.Include(i => i.Project)
            .WithKey(projectKey, number).FirstOrDefaultAsync(ct);
        if (blocker is null) return BadRequest($"there is no {dependsOnKey}");

        if (blocker.Id == issue.Id) return BadRequest("an issue cannot depend on itself");

        // Both directions of the tree, and both are upward walks: the blocker
        // is above this issue when climbing from this issue reaches it, and
        // below it when climbing from the blocker reaches this issue. A parent
        // is not done until its work is, so an edge either way round names a
        // dependency that could never be satisfied.
        if (await ClimbReachesAsync(issue.ParentId, blocker.Id, ct))
            return BadRequest($"{Format(blocker)} is above this issue - a dependency between them could never be satisfied");

        if (await ClimbReachesAsync(blocker.ParentId, issue.Id, ct))
            return BadRequest($"{Format(blocker)} is below this issue - a dependency between them could never be satisfied");

        if (await WaitsOnAsync(blocker.Id, issue.Id, ct))
            return BadRequest($"{Format(blocker)} is already waiting on this issue");

        var already = await db.Dependencies
            .AnyAsync(d => d.IssueId == issue.Id && d.DependsOnId == blocker.Id, ct);

        if (!already)
        {
            var now = time.GetUtcNow();
            var actor = await caller.ActorNameAsync(ct);

            db.Dependencies.Add(new EfHatchIssueDependency
            {
                IssueId = issue.Id,
                DependsOnId = blocker.Id,
                CreatedBy = actor,
                CreatedAt = now,
            });

            // On the issue that waits and on it alone: the edge is that
            // issue's, and a second event on the blocker would be the same
            // fact filed twice. The from/to shape is ParentChanged's, so the
            // page's event line needs no new case to read it.
            issue.Events.Add(Event(
                actor,
                EfHatchIssueEvent.DependencyAdded,
                new { from = (string?)null, to = Format(blocker) },
                now));

            issue.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return await IssueProjection.ToDtoAsync(db, actors, issue, claims, time.GetUtcNow(), ct);
    }

    /// <summary>
    /// Frees this issue from one. Removing an edge that is not there answers
    /// the same way an add of one that is already there does: nothing written,
    /// and the issue as it stands.
    /// </summary>
    [HttpDelete("{dependsOnKey}")]
    public async Task<ActionResult<IssueDto>> RemoveDependency(
        string key, string dependsOnKey, CancellationToken ct)
    {
        if (await LoadAsync(key, ct) is not { } issue) return NotFound();

        // A key that parses to nothing is not a refusal here, only an edge
        // that was never there: DELETE says what should not exist afterwards.
        if (IssueKey.TryParse(dependsOnKey, out var projectKey, out var number))
        {
            var edge = await db.Dependencies
                .Include(d => d.DependsOn).ThenInclude(i => i!.Project)
                .Where(d => d.IssueId == issue.Id)
                .FirstOrDefaultAsync(
                    d => d.DependsOn!.Project!.Key == projectKey && d.DependsOn.Number == number, ct);

            if (edge is not null)
            {
                var now = time.GetUtcNow();
                var actor = await caller.ActorNameAsync(ct);

                db.Dependencies.Remove(edge);
                issue.Events.Add(Event(
                    actor,
                    EfHatchIssueEvent.DependencyRemoved,
                    new { from = Format(edge.DependsOn!), to = (string?)null },
                    now));

                issue.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }
        }

        return await IssueProjection.ToDtoAsync(db, actors, issue, claims, time.GetUtcNow(), ct);
    }

    /// <summary>
    /// Whether climbing parents from <paramref name="from"/> reaches
    /// <paramref name="target"/>. A query per level, guarded by the visited set
    /// against a loop that got in some other way, which is
    /// <c>ResolveParentAsync</c>'s shape and is affordable for the same reason:
    /// the tree is three deep.
    /// </summary>
    private async Task<bool> ClimbReachesAsync(long? from, long target, CancellationToken ct)
    {
        var seen = new HashSet<long>();
        var at = from;

        while (at is { } id && seen.Add(id))
        {
            if (id == target) return true;
            at = await db.Issues.Where(i => i.Id == id).Select(i => i.ParentId).FirstOrDefaultAsync(ct);
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="start"/> already waits on <paramref name="target"/>,
    /// directly or through a chain - the cycle the new edge would close.
    /// </summary>
    /// <remarks>
    /// One read of every edge and then a walk in memory, rather than the query
    /// per level the tree walk uses. Unlike a parent chain, a dependency chain
    /// is as long as an epic is wide, and a query per link is a query per story.
    /// </remarks>
    private async Task<bool> WaitsOnAsync(long start, long target, CancellationToken ct)
    {
        var edges = await db.Dependencies.AsNoTracking()
            .Select(d => new { d.IssueId, d.DependsOnId })
            .ToListAsync(ct);

        var outward = edges
            .GroupBy(e => e.IssueId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.DependsOnId).ToList());

        var seen = new HashSet<long> { start };
        var frontier = new Queue<long>([start]);

        while (frontier.Count > 0)
        {
            if (!outward.TryGetValue(frontier.Dequeue(), out var next)) continue;

            foreach (var id in next)
            {
                if (id == target) return true;
                if (seen.Add(id)) frontier.Enqueue(id);
            }
        }

        return false;
    }

    private async Task<EfHatchIssue?> LoadAsync(string key, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return null;

        return await db.Issues.Include(i => i.Project).WithKey(projectKey, number).FirstOrDefaultAsync(ct);
    }

    private static string Format(EfHatchIssue issue) => IssueKey.Format(issue.Project!.Key, issue.Number);

    private static EfHatchIssueEvent Event(string actor, string kind, object? payload, DateTimeOffset at) => new()
    {
        Actor = actor,
        Kind = kind,
        Payload = payload is null ? null : JsonSerializer.Serialize(payload),
        At = at,
    };
}
