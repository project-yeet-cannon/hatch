using System.Text.Json;
using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// <em>This one first.</em> One flag on an issue, set by a person and honoured
/// by both halves of Hatch: the board floats the card to the top of its column,
/// and the dispatcher considers every expedited candidate before anything else.
///
/// It is a sort key and not a gate. Every fold an issue already meets still
/// folds it - see <see cref="EfHatchIssue.Expedited"/>, which is where that
/// argument is made and where the second thing it is not is written down.
/// </summary>
/// <remarks>
/// <para>Its own controller for the same reason
/// <see cref="AssigneeController"/> and <see cref="IssuePlaybookController"/>
/// are, and cut the same way: no class-level attribute, so the write below
/// inherits no scope from anything. <see cref="IssuesController"/> accepts the
/// <c>hatch</c> scope on every route it holds, so one more field on
/// <c>IssuePatchRequest</c> would have made this a key's write - and expedite
/// decides what the loop reaches for first, so a key that could set one could
/// put its own ticket at the front of every night (docs/hatch.md, "The one edge
/// that is deliberately cut"). It is cut in the route rather than asked for in
/// a prompt, because a rule an agent is merely told is a rule an agent can
/// reason its way past.</para>
///
/// <para>Reading is open, like everything else a dispatch needs: an agent is
/// entitled to know why it was sent where it was sent, and both
/// <see cref="IssueDto"/> and <see cref="IssueCardDto"/> carry the flag.</para>
///
/// <para>There is no <c>hatch expedite</c> at a terminal for the same reason:
/// the CLI authenticates with a key. The terminal shows the flag on
/// <c>board</c>, <c>queue</c> and <c>show</c>, and sets it nowhere.</para>
/// </remarks>
[ApiController]
[Route("api/hatch/issues")]
public class IssueExpediteController(
    HatchContext db, IActorDirectory actors, IssueClaims claims, ICallerIdentity caller,
    TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Mark it, or unmark it. The body says which, rather than the route
    /// meaning "the other one" - see <see cref="ExpediteRequest"/>.
    /// </summary>
    /// <remarks>
    /// Nothing else on the issue is touched, and nothing else touches this: a
    /// move, a retitle, a reparent and a close all leave the flag exactly as it
    /// was set, because it is written here and nowhere else.
    /// </remarks>
    [HttpPut("{key}/expedite")]
    [RequireAdmin]
    public async Task<ActionResult<IssueDto>> PutIssueExpedite(
        string key, ExpediteRequest request, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issue = await db.Issues.Include(i => i.Project).WithKey(projectKey, number).FirstOrDefaultAsync(ct);
        if (issue is null) return NotFound();

        // Setting the value the issue already holds writes nothing and does not
        // move UpdatedAt, the same as every other edit in Hatch - so a control
        // pressed twice on a card two people are looking at leaves one trail
        // entry and not three.
        if (request.Expedited != issue.Expedited)
        {
            var now = time.GetUtcNow();

            issue.Events.Add(new EfHatchIssueEvent
            {
                Actor = await caller.ActorNameAsync(ct),
                Kind = EfHatchIssueEvent.ExpeditedChanged,

                // Both sides, so the trail says which way it went rather than
                // only that somebody touched it. Spelled out rather than handed
                // a record, for the reason every other payload in this module
                // is - see AssigneeController.Side.
                Payload = JsonSerializer.Serialize(new { from = issue.Expedited, to = request.Expedited }),
                At = now,
            });

            issue.Expedited = request.Expedited;
            issue.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return await IssueProjection.ToDtoAsync(db, actors, issue, claims, time.GetUtcNow(), ct);
    }
}
