using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The level above the board: what a subtree adds up to, and what each of its
/// direct children adds up to.
///
/// Its own route rather than a wider board payload, because the board refetches
/// after every drag and already carries every card in the house - and because
/// this is the one read that answers "which project is nearest the line", which
/// is a different question from "what is on the board".
///
/// All of the arithmetic lives in <see cref="Rollup"/>. This controller resolves
/// a key, asks the tree, and shapes the answer.
/// </summary>
[ApiController]
[Route("api/hatch/plan")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class PlanController(HatchContext db, IssueClaims claims, TimeProvider time) : ControllerBase
{
    /// <summary>The type the Plan page is a list of. Everything else is what an epic is a total over.</summary>
    private const string Epic = "epic";

    /// <summary>
    /// One issue's progress and its children's, in one request - what the issue
    /// page draws under an epic or a story.
    /// </summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<IssueRollupDto>> GetIssuePlan(string key, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issue = await db.Issues.AsNoTracking()
            .WithKey(projectKey, number)
            .Select(i => new { i.Id, ProjectKey = i.Project!.Key, i.Number })
            .FirstOrDefaultAsync(ct);
        if (issue is null) return NotFound();

        var tree = await Rollup.LoadAsync(db, ct);

        var children = await db.Issues.AsNoTracking()
            .Where(i => i.ParentId == issue.Id)
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Id,
                ProjectKey = i.Project!.Key,
                i.Number,
                i.Type,
                i.Title,
                i.StatusId,
                i.Rank,
                i.ReadyAt,
                i.ReadyAtHasTime,
                i.DueAt,
                i.DueAtHasTime,
                Claim = new ClaimSnapshot(
                    i.ClaimToken, i.ClaimedBy, i.ClaimRunner,
                    i.ClaimedAt, i.ClaimHeartbeatAt, i.ClaimChatter, i.ClaimChatterAt),
            })
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var parentKey = IssueKey.Format(issue.ProjectKey, issue.Number);

        var rows = children.Select(c => new ChildRollupDto(
            new IssueCardDto(
                IssueKey.Format(c.ProjectKey, c.Number),
                c.ProjectKey,
                c.Type,
                c.Title,
                c.StatusId,
                c.Rank,
                parentKey,
                IssueMoment.Format(c.ReadyAt, c.ReadyAtHasTime),
                IssueMoment.Format(c.DueAt, c.DueAtHasTime),
                Claim: claims.Project(c.Claim, now)),
            tree.IsLeaf(c.Id),
            tree.Of(c.Id))).ToList();

        return new IssueRollupDto(parentKey, tree.Of(issue.Id), rows);
    }

    /// <summary>
    /// Every epic in the tracker with what it adds up to: the Plan page's one
    /// request, so a screen of twenty-eight meters is not twenty-eight
    /// questions.
    /// </summary>
    /// <param name="projectId">
    /// Whose epics to list, or every project's when absent. Both halves of the
    /// answer are scoped by it. A number naming no project comes back with an
    /// empty plan rather than a 404 - an empty project is a legal thing to look
    /// at, and so is one another tab has just deleted.
    /// </param>
    /// <remarks>
    /// One tree load answers the whole page. <see cref="Rollup.Tree"/> folds on
    /// demand and remembers, so asking it about every epic in the house costs
    /// one walk of the tracker rather than one per epic - which is the only
    /// reason this can be a single request at all.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<PlanDto>> GetPlan([FromQuery] int? projectId, CancellationToken ct)
    {
        var tree = await Rollup.LoadAsync(db, ct);

        var epics = await InProject(db.Issues.AsNoTracking().Where(i => i.Type == Epic), projectId)
            .Select(i => new
            {
                i.Id,
                i.ParentId,
                ProjectKey = i.Project!.Key,
                i.Number,
                i.Type,
                i.Title,
                i.StatusId,
                i.Rank,
                i.ReadyAt,
                i.ReadyAtHasTime,
                i.DueAt,
                i.DueAtHasTime,
                Claim = new ClaimSnapshot(
                    i.ClaimToken, i.ClaimedBy, i.ClaimRunner,
                    i.ClaimedAt, i.ClaimHeartbeatAt, i.ClaimChatter, i.ClaimChatterAt),
            })
            .ToListAsync(ct);

        // The work hanging under no epic at all, which is the roots that are
        // not epics - a bug filed on its own, a story somebody forgot to
        // parent, and everything beneath them.
        var looseRoots = await InProject(
                db.Issues.AsNoTracking().Where(i => i.ParentId == null && i.Type != Epic), projectId)
            .Select(i => i.Id)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var byId = epics.ToDictionary(e => e.Id);
        var keys = epics.ToDictionary(e => e.Id, e => IssueKey.Format(e.ProjectKey, e.Number));

        // An epic parented to something that is not an epic: the type rules
        // refuse it, so this is imported or hand-edited data. One query for all
        // of them, and none at all in the ordinary case - a card whose
        // parentKey came back null would send the page to no issue rather than
        // to the wrong one, which is the worse of the two.
        var strangers = epics.Select(e => e.ParentId).OfType<long>().Where(id => !keys.ContainsKey(id)).Distinct().ToList();
        if (strangers.Count > 0)
        {
            var found = await db.Issues.AsNoTracking()
                .Where(i => strangers.Contains(i.Id))
                .Select(i => new { i.Id, ProjectKey = i.Project!.Key, i.Number })
                .ToListAsync(ct);

            foreach (var row in found) keys[row.Id] = IssueKey.Format(row.ProjectKey, row.Number);
        }

        // Drawn once, wherever it is first reached. A single parent column
        // cannot put an epic in two places, but a cycle written around
        // parenting - a restored backup, a hand-written UPDATE - could
        // otherwise recurse forever, and a wrong tree beats one that never
        // finishes.
        var drawn = new HashSet<long>();

        return new PlanDto(
            Entries(epics.Where(e => e.ParentId is null).Select(e => e.Id).ToList()),
            tree.Of(looseRoots));

        List<PlanEntryDto> Entries(List<long> ids)
        {
            var entries = new List<PlanEntryDto>();

            foreach (var id in ids
                         .OrderBy(id => byId[id].ProjectKey, StringComparer.Ordinal)
                         .ThenBy(id => byId[id].Number))
            {
                if (!drawn.Add(id)) continue;

                var row = byId[id];

                entries.Add(new PlanEntryDto(
                    new IssueCardDto(
                        keys[id],
                        row.ProjectKey,
                        row.Type,
                        row.Title,
                        row.StatusId,
                        row.Rank,
                        row.ParentId is { } parent ? keys.GetValueOrDefault(parent) : null,
                        IssueMoment.Format(row.ReadyAt, row.ReadyAtHasTime),
                        IssueMoment.Format(row.DueAt, row.DueAtHasTime),
                        Claim: claims.Project(row.Claim, now)),
                    tree.IsLeaf(id),
                    // The whole subtree, not the epics below it: an epic's
                    // meter is its stories and their tasks, and the nested
                    // epics are only the part of it that is drawn again.
                    tree.Of(id),
                    Entries(Nested(id))));
            }

            return entries;
        }

        // The epics below this one whose nearest epic ancestor it is. A
        // generation at a time, stopping at each epic rather than descending
        // through it, because that epic draws its own subtree - so every epic
        // lands in exactly one list and the page draws the tree once.
        List<long> Nested(long id)
        {
            var seen = new HashSet<long> { id };
            var found = new List<long>();
            var generation = new List<long> { id };

            while (generation.Count > 0)
            {
                var next = new List<long>();

                foreach (var parent in generation)
                foreach (var child in tree.ChildrenOf(parent))
                {
                    if (!seen.Add(child)) continue;

                    if (byId.ContainsKey(child)) found.Add(child);
                    else next.Add(child);
                }

                generation = next;
            }

            return found;
        }
    }

    /// <summary>The optional project filter, applied the once for both halves of the plan.</summary>
    private static IQueryable<EfHatchIssue> InProject(IQueryable<EfHatchIssue> issues, int? projectId) =>
        projectId is { } id ? issues.Where(i => i.ProjectId == id) : issues;
}
