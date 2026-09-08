using System.Globalization;
using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The runners, and the one way the board reaches one: it writes down what it
/// would like, and the runner asks for it between increments.
///
/// Nothing here starts or stops a process. A <c>PATCH</c> setting
/// <c>stopping</c> is a row in a table until the loop that row names heartbeats
/// again - which it does at the top of its next pass, after whatever increment
/// was in flight has finished - and then it is the loop that exits. That is
/// what makes this work for every runner, in a container or on somebody's
/// laptop behind a router nothing can reach.
/// </summary>
/// <remarks>
/// <para>Its own controller for the attribute it does not carry at the class
/// level. The heartbeat and the read are a key's, exactly like the claim: a
/// dispatcher that could not say it was alive would leave a page that could
/// only ever be empty. The <c>PATCH</c> is a person's, for the reason
/// <see cref="IssuePlaybookController"/> and <see cref="AssigneeController"/>
/// are: an agent that could raise its own <c>--max-spend</c> could raise its
/// own budget, and a loop with no end is exactly what the bounds exist to
/// prevent.</para>
///
/// <para>There is no <c>DELETE</c>. A row ages out of the read on its own - see
/// <see cref="Runners"/> - and a deregister verb would be a second way for a
/// runner to disappear, one of which an operator could press while the process
/// was still running.</para>
/// </remarks>
[ApiController]
[Route("api/hatch/runners")]
public class RunnersController(
    HatchContext db, Runners runners, IssueClaims claims, ICallerIdentity caller, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Every runner that has spoken to this Hatch lately, most recently heard
    /// from first.
    /// </summary>
    /// <remarks>
    /// Two reads and no join. The claims are every issue holding one - tens of
    /// cards on the busiest board this is for - matched to runners in memory,
    /// because the thing being matched on is a name a checkout computed and not
    /// a key anybody indexed.
    /// </remarks>
    [HttpGet]
    [RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
    public async Task<ActionResult<IReadOnlyList<RunnerDto>>> GetRunners(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // The drop horizon in the WHERE, so a runner nobody has heard from
        // since last night is not something a client has to know to filter out.
        // The row stays where it is: one that comes back under the same name
        // finds its own bounds still on it.
        var dropBefore = runners.DropBefore(now);

        var rows = await db.Runners.AsNoTracking()
            .Where(r => r.LastSeenAt >= dropBefore)
            .OrderByDescending(r => r.LastSeenAt)
            .ToListAsync(ct);

        var held = await HeldAsync(now, ct);

        return rows.Select(r => runners.Project(r, Holding(held, r.Name))).ToList();
    }

    /// <summary>
    /// Still here - and what should I do next. The only call a runner makes
    /// about itself, and the whole of the round trip: it says what it is, and
    /// reads back what the board would like.
    /// </summary>
    /// <remarks>
    /// <para>The four bounds in the body are what this process started with,
    /// and they are written <em>only when the row is created</em>. That is the
    /// one subtle thing in this file: a loop restarting overnight sends its
    /// original <c>--max-runs</c> on every incarnation's first heartbeat, and a
    /// row that re-seeded would quietly undo the edit an operator made at
    /// midnight.</para>
    ///
    /// <para>Nothing here is refused for being unreasonable. A heartbeat is not
    /// a decision, and a night that ended because a runner sent a line somebody
    /// would call too long would be the wrong trade twice over - so the line is
    /// truncated, a bound that makes no sense is dropped, and only a nameless
    /// runner is turned away.</para>
    /// </remarks>
    [HttpPost("{name}")]
    [RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
    public async Task<ActionResult<RunnerInstructionDto>> Heartbeat(
        string name, RunnerHeartbeatRequest request, CancellationToken ct)
    {
        if (Named(name) is not { } runner) return BadRequest(NameRefusal);

        var kind = request.Kind?.Trim().ToLowerInvariant() switch
        {
            EfHatchRunner.OnceKind => EfHatchRunner.OnceKind,
            // A heartbeat that did not say reads as a loop. The kind decides
            // whether a page draws controls at all, and a loop drawn without
            // them is the feature missing; a single increment drawn with them
            // is two buttons nothing will ever read.
            _ => EfHatchRunner.LoopKind,
        };

        var now = time.GetUtcNow();
        var line = Runners.Normalise(request.Line);

        var row = await db.Runners.FirstOrDefaultAsync(r => r.Name == runner, ct);
        if (row is null)
        {
            db.Runners.Add(row = Seed(runner, kind, line, request, now));

            try
            {
                await db.SaveChangesAsync(ct);
                return Runners.Instruct(row);
            }
            catch (DbUpdateException)
            {
                // Two processes in one checkout - a loop and a `hatch work`
                // beside it - can both find no row and both insert one. The
                // second loses on the primary key and becomes what it should
                // have been, which is an update: it does not re-seed, so the
                // bounds on the row are the ones that got there first.
                db.Entry(row).State = EntityState.Detached;
                row = await db.Runners.FirstOrDefaultAsync(r => r.Name == runner, ct);
                if (row is null) throw;
            }
        }

        Touch(row, kind, line, now);
        await db.SaveChangesAsync(ct);

        return Runners.Instruct(row);
    }

    /// <summary>
    /// Keep going, pause, stop after this one - and the bounds that are flags
    /// at a terminal.
    /// </summary>
    /// <remarks>
    /// The order of the body is the guarantee that a refused request writes
    /// nothing: every present field is parsed and judged before the row is
    /// touched, so a request naming a good state and a nonsense budget changes
    /// neither.
    ///
    /// <para>It creates nothing. A name no runner has ever used is a <c>404</c>
    /// rather than a row waiting for a process that may never exist - the table
    /// is a record of what has spoken to this Hatch, and an operator typing a
    /// hostname into a URL is not a runner.</para>
    /// </remarks>
    [HttpPatch("{name}")]
    [RequireAdmin]
    public async Task<ActionResult<RunnerDto>> PatchRunner(
        string name, RunnerPatchRequest request, CancellationToken ct)
    {
        if (await NotAPerson(ct) is { } refusal) return refusal;
        if (Named(name) is not { } runner) return BadRequest(NameRefusal);

        var row = await db.Runners.FirstOrDefaultAsync(r => r.Name == runner, ct);
        if (row is null) return NotFound();

        // Absent leaves a field alone, "" clears it, a value sets it - the
        // convention the two dates on an issue already established, and the
        // reason the numbers are strings on the wire: a nullable number can say
        // "leave it" or "set it", but not "take the cap off".
        string? state = null;
        if (request.State is not null)
        {
            state = request.State.Trim().ToLowerInvariant();
            if (!EfHatchRunner.States.Contains(state))
                return BadRequest($"a runner is {string.Join(", ", EfHatchRunner.States)} - not \"{request.State.Trim()}\"");
        }

        string? under = null;
        if (request.Under is { } wanted && wanted.Trim().Length > 0)
        {
            under = wanted.Trim().ToUpperInvariant();
            if (!IssueKey.TryParse(under, out _, out _))
                return BadRequest($"--under names an issue, like AER-12 - not \"{wanted.Trim()}\"");
        }

        if (Number(request.MaxRuns, out var maxRuns) is { } runsError) return BadRequest(runsError);
        if (Money(request.MaxSpend, out var maxSpend) is { } spendError) return BadRequest(spendError);

        DateTimeOffset? untilAt = null;
        if (request.UntilAt is { } until && until.Trim().Length > 0)
        {
            // An instant, and the same parser a ready date is read with. A stop
            // hour is never a bare calendar date - "stop by the 12th" would be
            // midnight in a timezone nobody named - so the date-only form is
            // refused rather than pinned to one.
            if (!IssueMoment.TryParse(until, out var moment) || !moment.HasTime)
                return BadRequest("--until takes an instant, like 2026-09-12T17:00:00Z");

            untilAt = moment.At;
        }

        if (state is not null) row.State = state;
        if (request.Under is not null) row.Under = under;
        if (request.MaxRuns is not null) row.MaxRuns = maxRuns;
        if (request.MaxSpend is not null) row.MaxSpend = maxSpend;
        if (request.UntilAt is not null) row.UntilAt = untilAt;

        await db.SaveChangesAsync(ct);

        var now = time.GetUtcNow();
        return runners.Project(row, Holding(await HeldAsync(now, ct), row.Name));
    }

    // ---- The one narrowing ----

    /// <summary>
    /// A person, not a key. Every field this route writes is a bound on how much
    /// a runner may spend and how long it may run, so an agent that could reach
    /// it could raise its own budget - which is the failure the bounds exist to
    /// prevent, reintroduced through the thing that reads them.
    /// </summary>
    /// <remarks>
    /// Checked in the action as well as declared in the attribute, for
    /// <c>IssueClaimController.NotAPerson</c>'s reason: <c>AdminGate</c> is
    /// dormant wherever <c>Auth:EnforceAdmin</c> is off, which is all of local
    /// development, and a guarantee that evaporates under a switch is not a
    /// guarantee. A keyless runner that named itself with the runner header is
    /// refused here too - it is an agent whether or not it carries a
    /// credential.
    /// </remarks>
    private async Task<ObjectResult?> NotAPerson(CancellationToken ct) =>
        await caller.IsProgramAsync(ct)
            ? new ObjectResult("what a runner may spend is the operator's to set, not an agent's")
            {
                StatusCode = StatusCodes.Status403Forbidden,
            }
            : null;

    // ---- The name ----

    private static readonly string NameRefusal =
        $"a runner names itself, as host:/path/to/checkout, in at most {EfHatchRunner.MaxNameLength} characters";

    /// <summary>
    /// The runner a route segment names, or null where it names nothing usable.
    /// </summary>
    /// <remarks>
    /// Unescaped once, because a runner name has slashes in it and Kestrel
    /// leaves <c>%2F</c> encoded in the path on purpose - decoding it would
    /// change how many segments a URL has. Everything else in the segment
    /// arrived decoded already, and unescaping a string with no percent left in
    /// it does nothing, so this is one call rather than a special case.
    /// </remarks>
    private static string? Named(string name)
    {
        var runner = Uri.UnescapeDataString(name ?? "").Trim();
        return runner.Length is > 0 and <= EfHatchRunner.MaxNameLength ? runner : null;
    }

    // ---- The rows ----

    /// <summary>A row that has never been seen before, seeded with what the process started with.</summary>
    private static EfHatchRunner Seed(
        string name, string kind, string? line, RunnerHeartbeatRequest request, DateTimeOffset now) => new()
    {
        Name = name,
        Kind = kind,
        FirstSeenAt = now,
        LastSeenAt = now,
        Line = line is { Length: > 0 } ? line : null,
        LineAt = line is { Length: > 0 } ? now : null,
        State = EfHatchRunner.Running,

        // What the flags said, so the row shows the bounds it is actually
        // running under from its first appearance rather than reading as
        // unbounded until somebody sets something.
        Under = Fits(request.Under, EfHatchRunner.MaxUnderLength),
        MaxRuns = request.MaxRuns is > 0 ? request.MaxRuns : null,
        MaxSpend = request.MaxSpend is > 0 ? request.MaxSpend : null,
        UntilAt = request.UntilAt,
    };

    /// <summary>
    /// A row that already exists: three columns and no more. The state and the
    /// four bounds are the operator's, and a heartbeat that wrote one would be
    /// the runner having the last word on what it may spend.
    /// </summary>
    private static void Touch(EfHatchRunner row, string kind, string? line, DateTimeOffset now)
    {
        row.Kind = kind;
        row.LastSeenAt = now;

        switch (line)
        {
            case null: break;
            case "":
                row.Line = null;
                row.LineAt = null;
                break;
            default:
                row.Line = line;
                row.LineAt = now;
                break;
        }
    }

    /// <summary>
    /// Every live claim, by the runner holding it. The key is
    /// <see cref="EfHatchIssue.ClaimRunner"/> read backwards, which is what
    /// makes "what is this runner working" a question with one answer and no
    /// second copy to keep in step.
    /// </summary>
    private async Task<Dictionary<string, (string Key, ClaimSnapshot Claim)>> HeldAsync(
        DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Issues.AsNoTracking()
            .Where(i => i.ClaimToken != null && i.ClaimRunner != null)
            .Select(i => new
            {
                i.Project!.Key,
                i.Number,
                i.ClaimToken,
                i.ClaimedBy,
                i.ClaimRunner,
                i.ClaimedAt,
                i.ClaimHeartbeatAt,
                i.ClaimChatter,
                i.ClaimChatterAt,
            })
            .ToListAsync(ct);

        return rows
            .Select(i => (
                Runner: i.ClaimRunner!,
                Key: IssueKey.Format(i.Key, i.Number),
                Claim: new ClaimSnapshot(
                    i.ClaimToken, i.ClaimedBy, i.ClaimRunner, i.ClaimedAt,
                    i.ClaimHeartbeatAt, i.ClaimChatter, i.ClaimChatterAt)))
            .Where(i => claims.IsLive(i.Claim, now))
            // A runner holds one ticket at a time by construction, and the most
            // recent heartbeat is the one to draw if something ever leaves two
            // behind - a row about the wrong ticket is worse than a row about
            // none.
            .GroupBy(i => i.Runner, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(i => i.Claim.HeartbeatAt)
                    .Select(i => (i.Key, i.Claim))
                    .First(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// What this runner holds, or null. Written out rather than reached for
    /// with <c>GetValueOrDefault</c>, which for a tuple hands back a pair of
    /// nulls that is not itself null - a claim on nothing, drawn as though it
    /// were one.
    /// </summary>
    private static (string Key, ClaimSnapshot Claim)? Holding(
        Dictionary<string, (string Key, ClaimSnapshot Claim)> held, string name) =>
        held.TryGetValue(name, out var claim) ? claim : null;

    // ---- The two numbers ----

    /// <summary>A count, or the sentence refusing one. Absent and <c>""</c> both come back null, and the caller tells them apart.</summary>
    private static string? Number(string? sent, out int? count)
    {
        count = null;
        if (sent is null || sent.Trim().Length == 0) return null;

        if (!int.TryParse(sent.Trim(), CultureInfo.InvariantCulture, out var runs) || runs < 0)
            return $"--max-runs takes a count - not \"{sent.Trim()}\"";

        count = runs;
        return null;
    }

    /// <summary>The same, in dollars.</summary>
    private static string? Money(string? sent, out decimal? amount)
    {
        amount = null;
        if (sent is null || sent.Trim().Length == 0) return null;

        if (!decimal.TryParse(sent.Trim(), CultureInfo.InvariantCulture, out var spend) || spend < 0)
            return $"--max-spend takes an amount in dollars - not \"{sent.Trim()}\"";

        // Two places, which is what the column holds. Rounded rather than
        // refused: an operator typing a third digit meant the amount, not a
        // lesson about scale.
        amount = decimal.Round(spend, 2, MidpointRounding.AwayFromZero);
        return null;
    }

    /// <summary>A reported value, capped rather than refused - see the remark on <see cref="Heartbeat"/>.</summary>
    private static string? Fits(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
