using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The board, in one request: every column and every card in the house.
///
/// One endpoint rather than a column-at-a-time read because the board is the
/// screen the operator lives on (docs/plans/pjm.md, "Goals") and it is refetched
/// after every action - two round trips per drag would be felt, and a board
/// assembled from separate reads can show a card in two columns at once.
/// </summary>
[ApiController]
[Route("api/hatch/board")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class BoardController(HatchContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<BoardDto>> GetBoard(CancellationToken ct)
    {
        var statuses = await db.Statuses.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .Select(s => new StatusDto(s.Id, s.Name, s.SortOrder, s.IsTerminal))
            .ToListAsync(ct);

        // Ordered by (StatusId, Rank, Id) so the client can slice the one list
        // into columns without sorting, and so two cards sharing a rank do not
        // trade places between refetches.
        var issues = await db.Issues.AsNoTracking()
            .OrderBy(i => i.StatusId)
            .ThenBy(i => i.Rank)
            .ThenBy(i => i.Id)
            .Select(i => new
            {
                ProjectKey = i.Project!.Key,
                i.Number,
                i.Type,
                i.Title,
                i.StatusId,
                i.Rank,
                ParentProjectKey = i.Parent == null ? null : i.Parent.Project!.Key,
                ParentNumber = i.Parent == null ? (int?)null : i.Parent.Number,
                i.ReadyAt,
                i.ReadyAtHasTime,
                i.DueAt,
                i.DueAtHasTime,
            })
            .ToListAsync(ct);

        var cards = issues.Select(i => new IssueCardDto(
            IssueKey.Format(i.ProjectKey, i.Number),
            i.ProjectKey,
            i.Type,
            i.Title,
            i.StatusId,
            i.Rank,
            i.ParentNumber is { } number ? IssueKey.Format(i.ParentProjectKey!, number) : null,
            IssueMoment.Format(i.ReadyAt, i.ReadyAtHasTime),
            IssueMoment.Format(i.DueAt, i.DueAtHasTime))).ToList();

        return new BoardDto(statuses, cards);
    }
}
