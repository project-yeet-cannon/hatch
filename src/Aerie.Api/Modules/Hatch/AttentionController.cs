using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// What the loop is waiting on a person for: a pull request nobody has
/// reviewed, and a question nobody has answered.
///
/// The nav strip draws it on every page, so it has to be one request rather
/// than two. Two polls can be a poll interval apart, and a badge counting one
/// instant beside a panel drawn from another shows up as a lit control whose
/// list is empty - which is the one failure a widget like this cannot recover
/// from, because after it happens twice nobody reads it again.
/// </summary>
/// <remarks>
/// Neither half restates a rule that already lives somewhere. Which column is
/// the review column is <see cref="Columns.AwaitingReview"/>'s answer, measured
/// off the board's shape and not off a name, and what makes a question open is
/// <see cref="Questions.Open"/>'s - the same call the board badges a card with
/// and the dispatcher refuses a ticket on. A browser that derived either for
/// itself would be the same rule written twice, in two languages, and the two
/// would drift.
/// </remarks>
[ApiController]
[Route("api/hatch/attention")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class AttentionController(HatchContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AttentionDto>> GetAttention(CancellationToken ct)
    {
        var statuses = await db.Statuses.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        // Every open question in the house, whatever shape the board is - a
        // question is waiting on somebody wherever its issue happens to stand.
        var questions = await Questions.ProjectAsync(db, Questions.Open(db), ct);

        // A board too short to have a review column has nothing in review, and
        // that is an answer rather than an error: the section draws its empty
        // state and the control stays quiet about a half that cannot exist here.
        if (Columns.AwaitingReview(statuses) is not { } review)
            return new AttentionDto([], 0, questions);

        // The column's own order, which is the board's: (Rank, Id), the same
        // ordering BoardController slices its columns with, so a row here sits
        // where the eye already found it on the board.
        var inReview = await db.Issues.AsNoTracking()
            .Where(i => i.StatusId == review.Id)
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Id)
            .Select(i => new
            {
                ProjectKey = i.Project!.Key,
                i.Number,
                i.Type,
                i.Title,
                i.PullRequestUrl,
            })
            .ToListAsync(ct);

        // Split rather than filtered twice: the ones without a link are not
        // dropped, they are counted, and the empty state says how many. An
        // issue that has sat in review for a month with nowhere to review it is
        // worth saying out loud without being worth lighting the strip up for.
        var reviews = inReview
            .Where(i => !string.IsNullOrWhiteSpace(i.PullRequestUrl))
            .Select(i => new ReviewDto(
                IssueKey.Format(i.ProjectKey, i.Number), i.Title, i.Type, i.PullRequestUrl!))
            .ToList();

        return new AttentionDto(reviews, inReview.Count - reviews.Count, questions);
    }
}
