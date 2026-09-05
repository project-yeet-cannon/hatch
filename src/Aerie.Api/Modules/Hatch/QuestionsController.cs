using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The questions waiting on a person - for one issue, or for the whole house.
///
/// Two routes on one controller rather than one each on two, because they are
/// the same read with a different <c>WHERE</c>, and the shape somebody answers
/// from should not depend on which door they came in by. The house-wide list is
/// the one that matters: an operator sitting down to unblock the board wants
/// every question at once, not a tour of the tickets that happen to have one.
/// </summary>
[ApiController]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class QuestionsController(HatchContext db) : ControllerBase
{
    /// <summary>
    /// Every question in the house, oldest first - which is answering order:
    /// the question that has been waiting longest is the one holding something
    /// up longest.
    /// </summary>
    /// <param name="open">
    /// True, the default, for the ones nobody has answered. False for all of
    /// them, answers included, which is what a reader catching up on decisions
    /// wants.
    /// </param>
    [HttpGet("/api/hatch/questions")]
    public async Task<ActionResult<IReadOnlyList<QuestionDto>>> GetQuestions([FromQuery] bool open = true, CancellationToken ct = default) =>
        await Questions.ProjectAsync(db, Source(open), ct);

    /// <summary>The same list, for one issue.</summary>
    [HttpGet("/api/hatch/issues/{key}/questions")]
    public async Task<ActionResult<IReadOnlyList<QuestionDto>>> GetIssueQuestions(string key, [FromQuery] bool open = true, CancellationToken ct = default)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issueId = await db.Issues.WithKey(projectKey, number).Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
        if (issueId is null) return NotFound();

        return await Questions.ProjectAsync(db, Source(open).Where(c => c.IssueId == issueId), ct);
    }

    private IQueryable<EfHatchComment> Source(bool open) =>
        open
            ? Questions.Open(db)
            : db.Comments.AsNoTracking().Where(c => c.Kind == EfHatchComment.Question);
}
