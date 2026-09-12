using System.Text.Json;
using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// The one thing about an issue that an agent may read and may not write: the
/// model and the effort every increment dispatched for it runs on.
///
/// A playbook prices a transition. This prices a ticket, for the story that
/// turns out to be harder than its column suggests - and it beats every
/// playbook that could speak for that ticket, not one transition's worth, so
/// it is still true at three in the morning when nobody is typing.
/// </summary>
/// <remarks>
/// Its own controller for one verb because of the class-level attribute it does
/// not carry. <see cref="IssuesController"/> accepts the <c>hatch</c> scope on
/// every route it holds, so two more fields on <c>IssuePatchRequest</c> would
/// be an agent that can raise its own budget - the exact edge
/// <see cref="PlaybooksController"/> cuts on purpose (docs/hatch.md, "The one
/// edge that is deliberately cut"). It is cut in the route rather than asked
/// for in a prompt, because a rule an agent is merely told is a rule an agent
/// can reason its way past.
///
/// Reading is open, like everything else a dispatch needs: an agent is entitled
/// to know what it is being spent on, and <see cref="IssueDto"/> carries both
/// fields.
/// </remarks>
[ApiController]
[Route("api/hatch/issues")]
public class IssuePlaybookController(
    HatchContext db, IActorDirectory actors, IssueClaims claims, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Set, change or clear either override. Null leaves a field alone and
    /// <c>""</c> - or whitespace, which trims to it - hands that field back to
    /// the playbook.
    /// </summary>
    /// <remarks>
    /// The order of the body is the guarantee that a refused request writes
    /// nothing: every present value is validated before the entity is touched,
    /// so a request naming a bad model and a good effort writes neither.
    /// </remarks>
    [HttpPatch("{key}/playbook")]
    [RequireAdmin]
    public async Task<ActionResult<IssueDto>> PatchIssuePlaybook(
        string key, IssuePlaybookRequest request, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issue = await db.Issues.Include(i => i.Project).WithKey(projectKey, number).FirstOrDefaultAsync(ct);
        if (issue is null) return NotFound();

        // Present-but-empty clears, as the dates and the pull request URL do.
        // A whitespace-only value is a clear too: nobody means "run it on
        // three spaces".
        var model = request.Model is null ? null : Blank(request.Model.Trim());
        var effort = request.Effort is null ? null : Blank(request.Effort.Trim());

        // Both refusals before either assignment, and each judged only on the
        // field it was sent for. The sentences are PlaybooksController's own -
        // an issue accepts exactly what a playbook accepts, because it is the
        // same rule called twice.
        if (model is not null && PlaybooksController.InvalidModel(model) is { } modelError)
            return BadRequest(modelError);

        if (effort is not null && PlaybooksController.InvalidEffort(effort) is { } effortError)
            return BadRequest(effortError);

        var now = time.GetUtcNow();
        var actor = await caller.ActorNameAsync(ct);
        var events = new List<EfHatchIssueEvent>();

        if (request.Model is not null && model != issue.ModelOverride)
        {
            events.Add(Event(
                actor,
                EfHatchIssueEvent.ModelOverrideChanged,
                new { from = issue.ModelOverride, to = model },
                now));

            issue.ModelOverride = model;
        }

        if (request.Effort is not null && effort != issue.EffortOverride)
        {
            events.Add(Event(
                actor,
                EfHatchIssueEvent.EffortOverrideChanged,
                new { from = issue.EffortOverride, to = effort },
                now));

            issue.EffortOverride = effort;
        }

        // Setting a field to what it already holds writes nothing and does not
        // move UpdatedAt, the same as every other edit in Hatch.
        if (events.Count > 0)
        {
            foreach (var e in events) issue.Events.Add(e);
            issue.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return await IssueProjection.ToDtoAsync(db, actors, issue, claims, time.GetUtcNow(), ct);
    }

    /// <summary>An emptied value read as the null the column holds for "no override".</summary>
    private static string? Blank(string trimmed) => trimmed.Length == 0 ? null : trimmed;

    private static EfHatchIssueEvent Event(string actor, string kind, object? payload, DateTimeOffset at) => new()
    {
        Actor = actor,
        Kind = kind,
        Payload = payload is null ? null : JsonSerializer.Serialize(payload),
        At = at,
    };
}
