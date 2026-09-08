using System.Text.Json;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The lease on an issue: taken before an increment, refreshed while it runs,
/// released after it - see <see cref="EfHatchIssue.ClaimToken"/> for what it is
/// and <see cref="IssueClaims"/> for the rule.
///
/// A key may take, refresh and release one, which is the whole point: the
/// caller is a dispatcher, and a mutex only an operator could operate would
/// mutex nothing. The one narrowing is the tokenless <c>DELETE</c> - see
/// <see cref="NotAPerson"/>.
/// </summary>
/// <remarks>
/// <para>There is no <c>GET</c>. The claim rides <see cref="IssueDto"/> and
/// <see cref="IssueCardDto"/>, which is where every client needs it anyway, and
/// a second route serving the same object would be a second thing to keep in
/// step - the same call <see cref="IssueDependenciesController"/> makes about
/// its edges.</para>
///
/// <para>Nothing here looks at the issue's column. A claim is a lease, not a
/// dispatch: <see cref="WorkController"/> is what refuses to send an agent at a
/// terminal column, and a claim that second-guessed it would be the same rule
/// in two places, disagreeing the first time somebody edited one of them.</para>
/// </remarks>
[ApiController]
[Route("api/hatch/issues/{key}/claim")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class IssueClaimController(
    HatchContext db, IssueClaims claims, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Takes the lease, or says who already has it.
    /// </summary>
    /// <remarks>
    /// A live claim refuses a second one <em>including the caller's own</em>.
    /// Re-claiming is not a thing a runner does - it heartbeats - and a take
    /// that quietly succeeded for the same runner would hide the case the
    /// feature exists to catch, which is two checkouts on one box under one key.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<ClaimTakenDto>> TakeClaim(string key, ClaimRequest request, CancellationToken ct)
    {
        if (await LoadAsync(key, ct) is not { } issue) return NotFound();

        var runner = request.Runner?.Trim() ?? "";
        if (runner.Length == 0) return BadRequest("a claim names the runner holding it");
        if (runner.Length > EfHatchIssue.MaxClaimRunnerLength)
            return BadRequest($"a runner is at most {EfHatchIssue.MaxClaimRunnerLength} characters");

        var now = time.GetUtcNow();

        // Read first, because the refusal has to name a holder and that
        // sentence can only be written from the row. The guarantee is not in
        // this read - it is in the predicate on the write below, which is what
        // makes two callers who both got here on the same pre-claim state
        // resolve to one claim.
        if (claims.IsLive(ClaimSnapshot.Of(issue), now))
            return Conflict(claims.Sentence(ClaimSnapshot.Of(issue), now));

        var token = Guid.NewGuid();
        var actor = await caller.ActorNameAsync(ct);

        if (!await claims.TryTakeAsync(db, issue.Id, token, actor, runner, now, ct))
        {
            // Somebody won the race between that read and this write. Say who,
            // from the row as it stands now - and where the re-read finds it
            // clear again, it was taken and released inside this request, which
            // is worth naming honestly rather than dressing up.
            var current = await SnapshotAsync(issue.Id, ct);
            return Conflict(claims.IsLive(current, now)
                ? claims.Sentence(current, now)
                : "somebody else claimed this first");
        }

        Log(issue, actor, EfHatchIssueEvent.ClaimTaken, null, Holder(actor, runner), now);
        await db.SaveChangesAsync(ct);

        return new ClaimTakenDto(token, actor, now, claims.TtlSeconds);
    }

    /// <summary>
    /// Still here. Refreshes the lease and, optionally, replaces the line it is
    /// carrying.
    /// </summary>
    /// <remarks>
    /// No event, deliberately. A heartbeat is not a decision, and the trail
    /// would be a row a minute for every running increment - the same argument
    /// the work log makes about meter readings.
    /// </remarks>
    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat(string key, ClaimHeartbeatRequest request, CancellationToken ct)
    {
        if (await LoadAsync(key, ct) is not { } issue) return NotFound();

        var now = time.GetUtcNow();
        var claim = ClaimSnapshot.Of(issue);

        if (claim.Token != request.Token || !claims.IsLive(claim, now))
            return Conflict(Why(claim, request.Token, now));

        // Normalised, then truncated rather than refused: the field is
        // cosmetic, and killing a live lease because a terminal printed
        // something wide would be the wrong trade.
        var chatter = Normalise(request.Chatter);

        // The predicate can still refuse - the lease may have expired or been
        // retaken between that read and this write - and it is what actually
        // fences the write, so its answer is the one that decides.
        return await claims.TryRefreshAsync(db, issue.Id, request.Token, chatter, now, ct)
            ? NoContent()
            : Conflict(Why(await SnapshotAsync(issue.Id, ct), request.Token, now));
    }

    /// <summary>
    /// Lets go of it. Two lanes: a runner presenting the token it was given,
    /// and the operator clearing whatever is there.
    /// </summary>
    /// <remarks>
    /// Releasing an issue that holds no claim is a <c>204</c> that writes
    /// nothing, in both lanes. Releasing twice is not an error, and neither is
    /// releasing a claim that expired underneath you - a <c>DELETE</c> says
    /// what should not exist afterwards, and it does not.
    ///
    /// <para>Nothing is returned. The caller is on its way out, and a
    /// projection it will not read is a query for nobody.</para>
    /// </remarks>
    /// <param name="token">
    /// The one the claim was taken with. Absent is the operator's clobber -
    /// see <see cref="NotAPerson"/>.
    /// </param>
    [HttpDelete]
    public async Task<IActionResult> ReleaseClaim(string key, [FromQuery] Guid? token, CancellationToken ct)
    {
        if (await LoadAsync(key, ct) is not { } issue) return NotFound();

        if (token is null && await NotAPerson(ct) is { } refusal) return refusal;

        var now = time.GetUtcNow();
        var claim = ClaimSnapshot.Of(issue);

        if (claim.Token is null) return NoContent();

        if (token is { } presented && presented != claim.Token)
            return Conflict(Why(claim, presented, now));

        var actor = await caller.ActorNameAsync(ct);
        var holder = Holder(claim.ClaimedBy, claim.Runner);

        var cleared = token is { } mine
            ? await claims.TryReleaseAsync(db, issue.Id, mine, ct)
            : await claims.ClearAsync(db, issue.Id, ct);

        // Nothing cleared means it went between the read and the write, which
        // is the state a DELETE was asking for. No event, because nothing here
        // did anything.
        if (!cleared) return NoContent();

        Log(
            issue,
            actor,
            token is null ? EfHatchIssueEvent.ClaimCleared : EfHatchIssueEvent.ClaimReleased,
            holder,
            null,
            now);

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- The one narrowing ----

    /// <summary>
    /// A person, not a key. The tokenless release takes a ticket off whoever is
    /// holding it, and an agent that could do that could take a ticket off
    /// another agent mid-increment - which is the exact failure the claim
    /// exists to prevent, reintroduced through its own back door. A runner lets
    /// go of its own lease by presenting its token, which every honest runner
    /// has.
    /// </summary>
    /// <remarks>
    /// Checked in the action rather than expressed in the attribute, for the
    /// reason <c>IssueWorkLogController.NotAKey</c> gives: <c>AdminGate</c> is
    /// dormant wherever <c>Auth:EnforceAdmin</c> is off, which is all of local
    /// development, and a guarantee that evaporates under a switch is not a
    /// guarantee.
    ///
    /// <para>"A key" is asked as
    /// <see cref="ICallerIdentity.IsProgramAsync"/>, so a keyless runner in
    /// local mode is refused here too - the failure this narrowing exists to
    /// prevent is an agent taking a ticket off another agent, and an agent
    /// without a credential can do that just as well as one with.</para>
    /// </remarks>
    private async Task<ObjectResult?> NotAPerson(CancellationToken ct) =>
        await caller.IsProgramAsync(ct)
            ? new ObjectResult("clearing another runner's claim is the operator's, not an agent's")
            {
                StatusCode = StatusCodes.Status403Forbidden,
            }
            : null;

    // ---- Saying why ----

    /// <summary>
    /// Why a token was refused, in the three shapes a runner can be refused in:
    /// the row carries nothing, the row carries somebody else's, or the row
    /// still carries this one and the lease is simply over.
    /// </summary>
    private string Why(ClaimSnapshot claim, Guid presented, DateTimeOffset now) => claim.Token switch
    {
        null => "this claim was cleared",
        var held when held == presented => "this claim has expired",
        _ => claims.IsLive(claim, now) ? claims.Sentence(claim, now) : "this claim was taken over",
    };

    // ---- Odds and ends ----

    /// <summary>
    /// One line at most, trimmed and capped. Null stays null - "no opinion" and
    /// "clear it" are different instructions and the empty string is the second
    /// one, so it survives to the write intact.
    /// </summary>
    private static string? Normalise(string? chatter)
    {
        if (chatter is null) return null;

        var line = chatter.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length > EfHatchIssue.MaxClaimChatterLength
            ? line[..EfHatchIssue.MaxClaimChatterLength]
            : line;
    }

    /// <summary>The holder as the trail names one: who, and from where.</summary>
    private static string Holder(string? who, string? runner) => $"{who} on {runner}";

    /// <summary>
    /// The claim columns as they stand now, read past the change tracker -
    /// which the tracked entity cannot answer for, because the writes in
    /// <see cref="IssueClaims"/> run outside it.
    /// </summary>
    private async Task<ClaimSnapshot> SnapshotAsync(long issueId, CancellationToken ct) =>
        await db.Issues.AsNoTracking()
            .Where(i => i.Id == issueId)
            .Select(i => new ClaimSnapshot(
                i.ClaimToken, i.ClaimedBy, i.ClaimRunner,
                i.ClaimedAt, i.ClaimHeartbeatAt, i.ClaimChatter, i.ClaimChatterAt))
            .FirstOrDefaultAsync(ct)
        ?? new ClaimSnapshot(null, null, null, null, null, null, null);

    /// <summary>
    /// The trail row, in <c>DependencyAdded</c>'s <c>{ from, to }</c> shape so
    /// the issue page's event line needs no new case to read it. A take is
    /// null-to-holder and both releases are holder-to-null; which of the two a
    /// release was is the kind's to say.
    /// </summary>
    private void Log(EfHatchIssue issue, string actor, string kind, string? from, string? to, DateTimeOffset at)
    {
        issue.Events.Add(new EfHatchIssueEvent
        {
            Actor = actor,
            Kind = kind,
            Payload = JsonSerializer.Serialize(new { from, to }),
            At = at,
        });

        issue.UpdatedAt = at;
    }

    private async Task<EfHatchIssue?> LoadAsync(string key, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return null;

        return await db.Issues.Include(i => i.Project).WithKey(projectKey, number).FirstOrDefaultAsync(ct);
    }
}
