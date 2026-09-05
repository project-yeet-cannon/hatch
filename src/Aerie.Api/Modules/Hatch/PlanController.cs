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
public class PlanController(HatchContext db) : ControllerBase
{
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
            })
            .ToListAsync(ct);

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
                IssueMoment.Format(c.DueAt, c.DueAtHasTime)),
            tree.IsLeaf(c.Id),
            tree.Of(c.Id))).ToList();

        return new IssueRollupDto(parentKey, tree.Of(issue.Id), rows);
    }
}
