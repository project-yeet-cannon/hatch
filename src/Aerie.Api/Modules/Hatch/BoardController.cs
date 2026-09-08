using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The board, in one request: every column and every card in the house.
///
/// One endpoint rather than a column-at-a-time read because the board is the
/// screen the operator lives on (docs/hatch.md, "Goals") and it is refetched
/// after every action - two round trips per drag would be felt, and a board
/// assembled from separate reads can show a card in two columns at once.
/// </summary>
[ApiController]
[Route("api/hatch/board")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class BoardController(
    HatchContext db, IActorDirectory actors, IssueClaims claims, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<BoardDto>> GetBoard(CancellationToken ct)
    {
        var statuses = await db.Statuses.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .Select(s => new StatusDto(s.Id, s.Name, s.SortOrder, s.IsTerminal, s.Color))
            .ToListAsync(ct);

        // Ordered by (StatusId, Rank, Id) so the client can slice the one list
        // into columns without sorting, and so two cards sharing a rank do not
        // trade places between refetches.
        // One grouped read for the whole board rather than a count per card.
        // A card that is waiting on somebody has to say so here: the board is
        // where the operator looks, and a question they cannot see is a question
        // they never answer.
        var waiting = await Questions.OpenCountsAsync(db, ct);

        var issues = await db.Issues.AsNoTracking()
            .OrderBy(i => i.StatusId)
            .ThenBy(i => i.Rank)
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
                ParentProjectKey = i.Parent == null ? null : i.Parent.Project!.Key,
                ParentNumber = i.Parent == null ? (int?)null : i.Parent.Number,
                i.ReadyAt,
                i.ReadyAtHasTime,
                i.DueAt,
                i.DueAtHasTime,
                i.AssigneePersonId,
                i.AssigneeApiKeyId,
                Claim = new ClaimSnapshot(
                    i.ClaimToken, i.ClaimedBy, i.ClaimRunner,
                    i.ClaimedAt, i.ClaimHeartbeatAt, i.ClaimChatter, i.ClaimChatterAt),
            })
            .ToListAsync(ct);

        // Two more queries for the whole board, however many cards it holds:
        // the directory is memoized per request, so this is not a lookup a card.
        // An id whose person has been deleted or whose key has been revoked
        // resolves to null and the card draws nothing - the same answer the
        // issue page and the dispatcher give, at the same instant.
        var assignees = new Dictionary<long, AssigneeDto?>();
        foreach (var i in issues)
            assignees[i.Id] = await IssueProjection.ToAssigneeAsync(actors, i.AssigneePersonId, i.AssigneeApiKeyId, ct);

        // One instant for the whole board, so two cards claimed a second apart
        // are not judged against two different clocks.
        var now = time.GetUtcNow();

        var cards = issues.Select(i => new IssueCardDto(
            IssueKey.Format(i.ProjectKey, i.Number),
            i.ProjectKey,
            i.Type,
            i.Title,
            i.StatusId,
            i.Rank,
            i.ParentNumber is { } number ? IssueKey.Format(i.ParentProjectKey!, number) : null,
            IssueMoment.Format(i.ReadyAt, i.ReadyAtHasTime),
            IssueMoment.Format(i.DueAt, i.DueAtHasTime),
            waiting.GetValueOrDefault(i.Id),
            assignees[i.Id],
            claims.Project(i.Claim, now))).ToList();

        return new BoardDto(statuses, cards);
    }
}
