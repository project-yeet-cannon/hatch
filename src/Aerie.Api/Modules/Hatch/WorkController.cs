using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// "What should an agent do next, and how?" - answered in one request.
///
/// Every part of that answer is decided here rather than in the shell, for the
/// reason the rank is decided on the server (docs/plans/pjm.md, "Rank
/// computation"): it keeps every client dumb. Which issue is next, which column
/// it is headed for, whether it may go there at all, and which playbook speaks
/// for the move are all questions about rows this process owns, and a script
/// that re-derived them would drift the first time a column was renamed.
/// </summary>
[ApiController]
[Route("api/hatch/work")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class WorkController(HatchContext db, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// The issue an unattended run should pick up: the top of the rightmost
    /// column that still has work an agent may do.
    ///
    /// Rightmost first, and that is a scheduling policy worth saying out loud.
    /// A board worked left to right starts everything and finishes nothing; one
    /// worked right to left pushes whatever is furthest along over the line
    /// before it opens anything new. The second is what a person does when they
    /// mean to ship.
    /// </summary>
    /// <param name="offsetMinutes">
    /// The caller's offset from UTC, so a ready date is read against the
    /// caller's calendar day and not the server's - the same rule the board
    /// follows (schedule.ts). Absent is UTC, which is right for a machine and
    /// close enough for anybody who does not say.
    /// </param>
    [HttpGet("next")]
    public async Task<ActionResult<WorkDto>> GetNextWork([FromQuery] int offsetMinutes = 0, CancellationToken ct = default)
    {
        var statuses = await OrderedStatusesAsync(ct);
        var today = DayNumber(time.GetUtcNow(), offsetMinutes);

        // Right to left, top to bottom. The first issue whose transition is
        // actually available wins; everything folded, blocked or terminal is
        // passed over rather than reported, because "nothing to do" is the
        // useful answer and a list of reasons is not.
        foreach (var status in Enumerable.Reverse(statuses))
        {
            if (Advance(statuses, status) is null) continue;

            var candidates = await db.Issues
                .Where(i => i.StatusId == status.Id)
                .OrderBy(i => i.Rank).ThenBy(i => i.Id)
                .Include(i => i.Project)
                .ToListAsync(ct);

            foreach (var issue in candidates)
            {
                if (IsWaiting(issue, today, offsetMinutes)) continue;

                var work = await ResolveAsync(issue, statuses, ct);
                if (work.Blocked is null) return work;
            }
        }

        return NoContent();
    }

    /// <summary>
    /// The same answer for an issue somebody named. Blocked or not, it is
    /// returned rather than refused: a person who asked for <c>AER-12</c> is
    /// owed the sentence saying why it cannot move, not a 404.
    /// </summary>
    [HttpGet("{key}")]
    public async Task<ActionResult<WorkDto>> GetWork(string key, CancellationToken ct)
    {
        if (!IssueKey.TryParse(key, out var projectKey, out var number)) return NotFound();

        var issue = await db.Issues.Include(i => i.Project)
            .WithKey(projectKey, number).FirstOrDefaultAsync(ct);
        if (issue is null) return NotFound();

        return await ResolveAsync(issue, await OrderedStatusesAsync(ct), ct);
    }

    // ---- Resolution ----

    private async Task<WorkDto> ResolveAsync(EfHatchIssue issue, List<EfHatchStatus> statuses, CancellationToken ct)
    {
        var from = statuses.First(s => s.Id == issue.StatusId);
        var to = Advance(statuses, from);

        var children = await db.Issues.Where(i => i.ParentId == issue.Id)
            .OrderBy(i => i.Rank).ThenBy(i => i.Id)
            .Select(i => new
            {
                ProjectKey = i.Project!.Key,
                i.Number, i.Type, i.Title, i.StatusId, i.Rank,
                i.ReadyAt, i.ReadyAtHasTime, i.DueAt, i.DueAtHasTime,
            })
            .ToListAsync(ct);

        var childCards = children.Select(c => new IssueCardDto(
            IssueKey.Format(c.ProjectKey, c.Number),
            c.ProjectKey,
            c.Type,
            c.Title,
            c.StatusId,
            c.Rank,
            IssueKey.Format(issue.Project!.Key, issue.Number),
            IssueMoment.Format(c.ReadyAt, c.ReadyAtHasTime),
            IssueMoment.Format(c.DueAt, c.DueAtHasTime))).ToList();

        var playbook = to is null ? null : await MatchAsync(from.Id, to.Id, issue.Type, ct);

        // Both halves in one read: the open ones decide whether an agent is
        // dispatched at all, and the answered ones are what it is dispatched
        // knowing.
        var questions = await Questions.ForIssueAsync(db, issue.Id, ct);
        var waiting = questions.Count(q => q.Answers.Count == 0);

        return new WorkDto(
            await IssueProjection.ToDtoAsync(db, issue, ct),
            ToStatusDto(from),
            to is null ? null : ToStatusDto(to),
            playbook is null ? null : PlaybooksController.ToDto(playbook),
            childCards,
            questions,
            Blocked(from, to, playbook, issue.Type, waiting));
    }

    /// <summary>
    /// Why an agent should not be spawned for this issue, or null when it
    /// should. Four refusals, and two of them are load-bearing rules of the
    /// whole loop rather than missing configuration: only the operator decides
    /// that something shipped, so a transition into a terminal column is not an
    /// agent's to make; and an issue holding an unanswered question is waiting
    /// on a person, so dispatching another agent at it would only produce a
    /// second session asking the same thing or guessing at the answer.
    /// </summary>
    /// <param name="waiting">
    /// Unanswered questions on the issue. Checked before the playbook, because
    /// "nobody has answered you" is a more useful sentence than "no playbook
    /// covers this" when both are true.
    /// </param>
    private static string? Blocked(EfHatchStatus from, EfHatchStatus? to, EfHatchPlaybook? playbook, string type, int waiting)
    {
        if (from.IsTerminal)
            return $"\"{from.Name}\" is where work ends - there is nothing after it";

        if (to is null)
            return $"there is no column after \"{from.Name}\", so there is nowhere for this to go";

        if (to.IsTerminal)
            return $"the next column is \"{to.Name}\", and only the operator moves work there";

        if (waiting > 0)
            return $"{waiting} unanswered question{(waiting == 1 ? "" : "s")} - it is waiting on a person, not on an agent";

        return playbook is null
            ? $"no playbook covers \"{from.Name}\" to \"{to.Name}\" for a {type} - add one on the Playbooks page"
            : null;
    }

    /// <summary>
    /// The column immediately to the right, or null at the end of the board.
    /// Terminal columns are returned rather than skipped - the refusal above
    /// wants to name the one it is refusing.
    /// </summary>
    private static EfHatchStatus? Advance(List<EfHatchStatus> statuses, EfHatchStatus from)
    {
        if (from.IsTerminal) return null;

        var at = statuses.FindIndex(s => s.Id == from.Id);
        return at >= 0 && at + 1 < statuses.Count ? statuses[at + 1] : null;
    }

    /// <summary>
    /// The playbook that speaks for this move. A row naming the issue's type
    /// beats a row naming every type, and ties go to the older row - so adding
    /// a specific rule never requires editing the general one.
    /// </summary>
    private async Task<EfHatchPlaybook?> MatchAsync(int from, int to, string type, CancellationToken ct)
    {
        var candidates = await db.Playbooks.AsNoTracking()
            .Include(p => p.FromStatus)
            .Include(p => p.ToStatus)
            .Where(p => p.FromStatusId == from && p.ToStatusId == to)
            .ToListAsync(ct);

        return candidates
            .Where(p => p.Covers(type))
            .OrderByDescending(p => p.Specificity)
            .ThenBy(p => p.Id)
            .FirstOrDefault();
    }

    // ---- Dates ----

    /// <summary>
    /// Whether the issue is still waiting for its ready date, by the board's
    /// rule: workable from the start of the day it names, whatever hour was
    /// set, in the caller's zone.
    /// </summary>
    private static bool IsWaiting(EfHatchIssue issue, long today, int offsetMinutes)
    {
        if (issue.ReadyAt is not { } ready) return false;

        // A bare date is read in UTC, where its components are the ones that
        // were typed; an instant is read in the caller's zone, where the hour
        // somebody meant is the hour they meant. IssueMoment.cs holds the other
        // half of this contract.
        var day = issue.ReadyAtHasTime ? DayNumber(ready, offsetMinutes) : DayNumber(ready, 0);
        return day > today;
    }

    private static long DayNumber(DateTimeOffset at, int offsetMinutes) =>
        (long)Math.Floor((at.ToUnixTimeSeconds() + (offsetMinutes * 60L)) / 86_400.0);

    // ---- Loading ----

    private Task<List<EfHatchStatus>> OrderedStatusesAsync(CancellationToken ct) =>
        db.Statuses.AsNoTracking().OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync(ct);

    private static StatusDto ToStatusDto(EfHatchStatus s) =>
        new(s.Id, s.Name, s.SortOrder, s.IsTerminal, s.Color);
}
