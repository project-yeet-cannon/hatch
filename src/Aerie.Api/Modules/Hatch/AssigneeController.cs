using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// Who owns a ticket: read by anybody a dispatch reaches, written only by a
/// person.
///
/// An assignee is a durable statement - somebody's name on a card, changed
/// rarely, surviving every restart. It is deliberately not a claim, which is a
/// machine lease that comes and goes with an increment and shares no column with
/// this.
/// </summary>
/// <remarks>
/// <para>Its own controller for the same reason
/// <see cref="IssuePlaybookController"/> is, and cut the same way: no
/// class-level attribute, so the write below inherits no scope from anything.
/// <see cref="IssuesController"/> accepts the <c>hatch</c> scope on every route
/// it holds, so two more fields on <c>IssuePatchRequest</c> would have made this
/// a key's write - and under the loop's <c>people only</c> rule an assignee is a
/// dispatch gate. A key that could write one could clear a person's name off a
/// ticket and hand itself work that was reserved (docs/hatch.md, "The one edge
/// that is deliberately cut"). It is cut in the route rather than asked for in a
/// prompt, because a rule an agent is merely told is a rule an agent can reason
/// its way past.</para>
///
/// <para>Reading is open, like everything else a dispatch needs: an agent has to
/// know whose work it is about to take, and that is precisely the fact that
/// tells it to leave the ticket alone.</para>
/// </remarks>
[ApiController]
[Route("api/hatch")]
public class AssigneeController(
    HatchContext db, IActorDirectory actors, IssueClaims claims, ICallerIdentity caller,
    TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Everybody an issue could belong to, and who the caller is - in one read,
    /// because the picker needs the first and <em>Assign to me</em> needs the
    /// second, and two requests to draw one row is two chances to disagree.
    /// </summary>
    /// <remarks>
    /// Open to the <c>hatch</c> scope, unlike the write below. Nothing here is a
    /// power: it is a list of names an operator could have read off the People
    /// page, and refusing it would only mean an agent could not say who a ticket
    /// belongs to when it declines to take it.
    /// </remarks>
    [HttpGet("assignees")]
    [RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
    public async Task<ActionResult<AssigneeDirectoryDto>> GetAssignees(CancellationToken ct)
    {
        var live = await actors.LiveAsync(ct);
        var me = await actors.MeAsync(ct);

        // Me is answered from the directory rather than handed back from the
        // caller directly, so somebody signed in as a person who has since been
        // deleted - or holding a key revoked mid-session - is not offered a
        // press that would immediately be refused.
        var mine = me is null ? null : live.FirstOrDefault(a => a.Kind == me.Kind && a.Id == me.Id);

        return new AssigneeDirectoryDto(
            mine is null ? null : ToDto(mine),
            live.Select(ToDto).ToList());
    }

    /// <summary>
    /// Give the issue to somebody, hand it to somebody else, or take it off
    /// everybody - one route for all three, because they are one fact being set.
    /// </summary>
    /// <remarks>
    /// The order of the body is the guarantee that a refused request writes
    /// nothing: the key is parsed, the issue loaded, the shape judged and the
    /// identity resolved before either column is touched. Resolving is also what
    /// refuses a revoked key, and it is the same predicate every reader applies -
    /// so an assignee this route accepts is one the board will draw.
    /// </remarks>
    [HttpPut("issues/{key}/assignee")]
    [RequireAdmin]
    public async Task<ActionResult<IssueDto>> PutIssueAssignee(
        string key, AssigneeRequest request, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issue = await db.Issues.Include(i => i.Project).WithKey(projectKey, number).FirstOrDefaultAsync(ct);
        if (issue is null) return NotFound();

        var kind = request.Kind?.Trim();
        var wantsNobody = string.IsNullOrEmpty(kind) && request.Id is null;

        // Half a request is refused rather than guessed at. A kind with no id
        // meant something; the one thing it cannot mean is "nobody", because
        // nobody is already written as neither.
        if (!wantsNobody && (string.IsNullOrEmpty(kind) || request.Id is null))
            return BadRequest("an assignee needs a kind and an id, or neither");

        Actor? actor = null;
        if (!wantsNobody)
        {
            if (!ActorKind.IsKnown(kind))
                return BadRequest($"an assignee is a \"{ActorKind.Person}\" or a \"{ActorKind.Key}\"");

            actor = await actors.ResolveAsync(kind, request.Id, ct);

            // The liveness rule, reached from the writing end: a deleted person
            // and a revoked key are both "there is no such", because that is
            // what they read as everywhere else the moment after this.
            if (actor is null)
                return BadRequest(kind == ActorKind.Person ? "there is no such person" : "there is no such key");
        }

        // Setting one clears the other, which is where the check constraint's
        // invariant is actually maintained - the constraint is the backstop.
        var personId = actor?.Kind == ActorKind.Person ? actor.Id : (Guid?)null;
        var apiKeyId = actor?.Kind == ActorKind.Key ? actor.Id : (Guid?)null;

        // Setting the value an issue already holds writes nothing and does not
        // move UpdatedAt, the same as every other edit in Hatch.
        if (personId != issue.AssigneePersonId || apiKeyId != issue.AssigneeApiKeyId)
        {
            var now = time.GetUtcNow();
            var was = await IssueProjection.ToAssigneeAsync(
                actors, issue.AssigneePersonId, issue.AssigneeApiKeyId, ct);

            issue.Events.Add(new EfHatchIssueEvent
            {
                Actor = await caller.ActorNameAsync(ct),
                Kind = EfHatchIssueEvent.AssigneeChanged,

                // Each side carries the name as well as the id, because the
                // trail has to read after the row it named is gone - see
                // EfHatchIssueEvent.AssigneeChanged.
                Payload = JsonSerializer.Serialize(new { from = Side(was), to = Side(actor) }),
                At = now,
            });

            issue.AssigneePersonId = personId;
            issue.AssigneeApiKeyId = apiKeyId;
            issue.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
        }

        return await IssueProjection.ToDtoAsync(db, actors, issue, claims, time.GetUtcNow(), ct);
    }

    /// <summary>The module's wire shape for a platform actor - see <see cref="AssigneeDto"/>.</summary>
    private static AssigneeDto ToDto(Actor actor) => new(actor.Kind, actor.Id, actor.Name);

    /// <summary>
    /// One side of the event payload, or null for nobody.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than handed the DTO, because the payload is stored as
    /// jsonb and read back as raw JSON: a record would serialize with whatever
    /// casing the serializer happens to be configured for, and the trail's shape
    /// would then be a property of a setting somewhere else. Every other event
    /// kind in this module names its fields the same way.
    /// </remarks>
    private static object? Side(Actor? actor) =>
        actor is null ? null : new { kind = actor.Kind, id = actor.Id, name = actor.Name };

    /// <summary>The same, for an assignee already rendered by the projection.</summary>
    private static object? Side(AssigneeDto? assignee) =>
        assignee is null ? null : new { kind = assignee.Kind, id = assignee.Id, name = assignee.Name };
}
