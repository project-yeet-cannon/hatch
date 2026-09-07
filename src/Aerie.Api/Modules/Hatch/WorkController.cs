using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// "What should an agent do next, and how?" - answered in one request, and
/// "what would a whole pass do, and what would it skip?" in another.
///
/// Every part of that answer is decided here rather than in the shell, for the
/// reason the rank is decided on the server (docs/hatch.md, "Rank
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
    /// <param name="ancestorKey">
    /// One corner of the board instead of all of it: the same question asked
    /// only of the issues below this key, at any depth, so an evening can be
    /// pointed at one epic. Absent is the whole board, which is what every
    /// caller before this asked for.
    ///
    /// <para>Nothing else changes - still right to left, still top of the
    /// column down, still folding past a ready date, an open question, a
    /// terminal column, a type the loop does not take, or a sibling already
    /// awaiting review. The scope narrows the candidates and decides nothing
    /// about them.</para>
    ///
    /// <para>The issue itself is not a candidate. "Under AER-1" is a question
    /// about what hangs beneath it, which is how <c>ancestorKey</c> already
    /// reads on the search endpoint.</para>
    /// </param>
    /// <param name="types">
    /// Which types an unattended run may pick up, comma separated. Absent is
    /// <see cref="LoopTypes"/>; a type nobody defined is a 400 naming it rather
    /// than a filter that silently matches nothing.
    ///
    /// <para>A widening, and it belongs to the caller because the narrow set is
    /// the loop's policy rather than a fact about the board: an operator who
    /// means to have an evening spent on tasks says so.</para>
    /// </param>
    [HttpGet("next")]
    public async Task<ActionResult<WorkDto>> GetNextWork(
        [FromQuery] int offsetMinutes = 0,
        [FromQuery] string? ancestorKey = null,
        [FromQuery] string? types = null,
        CancellationToken ct = default)
    {
        var scan = await ScanAsync(offsetMinutes, ancestorKey, types, ct);
        if (scan.Failure is not null) return BadRequest(scan.Failure);

        // The first clear row of the queue, and nothing else. Not a second
        // walk that happens to agree with the scan's - the whole point of
        // publishing the scan is that it explains what this line picked, and
        // two loops that could disagree about the order of the board is
        // precisely the bug it exists to expose.
        var clear = scan.Rows.FirstOrDefault(r => r.Blocked is null);
        if (clear is null) return NoContent();

        return await ResolveAsync(clear.Issue, scan.Statuses, scan.Loop, ct);
    }

    /// <summary>
    /// What a whole pass would do, rather than what its first step is: every
    /// issue the dispatcher considers, in the order it considers them, each
    /// carrying the sentence saying why it cannot be advanced - or nothing,
    /// where it can.
    ///
    /// <para><c>next</c> folds past everything blocked in silence, and for one
    /// increment that is right: "nothing to do" is the useful answer and a list
    /// of reasons is noise. For an unattended loop it is backwards. Nobody is
    /// watching, and the one thing worth having afterwards is what the pass
    /// skipped and why - above all a column and type nobody has written a
    /// playbook for, which reads as a finished board and is not one.</para>
    ///
    /// <para>The first entry with no reason is what <see cref="GetNextWork"/>
    /// returns for the same arguments, because it is the same walk. The
    /// arguments mean exactly what they mean there.</para>
    /// </summary>
    /// <remarks>
    /// The columns with nowhere to go - a terminal one, and a rightmost one
    /// that is not terminal - are absent rather than listed as blocked. An
    /// issue the dispatcher never reaches is not something the pass skipped,
    /// and shipped work is not a backlog.
    /// </remarks>
    [HttpGet("queue")]
    public async Task<ActionResult<IReadOnlyList<QueueEntryDto>>> GetQueue(
        [FromQuery] int offsetMinutes = 0,
        [FromQuery] string? ancestorKey = null,
        [FromQuery] string? types = null,
        CancellationToken ct = default)
    {
        var scan = await ScanAsync(offsetMinutes, ancestorKey, types, ct);
        if (scan.Failure is not null) return BadRequest(scan.Failure);

        // One projection for the whole list. The per-issue one would be three
        // queries a row, which is what makes a scan of a board a thing nobody
        // runs twice.
        var issues = await IssueProjection.ToDtosAsync(db, scan.Rows.Select(r => r.Issue).ToList(), ct);

        return scan.Rows.Select(r => new QueueEntryDto(
            issues[r.Issue.Id],
            ToStatusDto(r.From),
            r.To is null ? null : ToStatusDto(r.To),
            r.Blocked)).ToList();
    }

    // ---- The walk ----

    /// <summary>
    /// The one pass over the board that both work endpoints are built on:
    /// right to left, top of the column down, every issue judged once.
    /// </summary>
    /// <remarks>
    /// Everything a row is judged against is read once here rather than once
    /// per row - the statuses, the scope, what is awaiting review, how many
    /// questions each issue is waiting on, and the whole playbook matrix. A
    /// board's worth of rows against a handful of queries, because a scan that
    /// cost a query a row would be a scan nobody leaves running.
    /// </remarks>
    private async Task<Scan> ScanAsync(int offsetMinutes, string? ancestorKey, string? types, CancellationToken ct)
    {
        var statuses = await OrderedStatusesAsync(ct);

        if (!TryReadTypes(types, out var wanted, out var unknown))
            return Scan.Refused($"there is no \"{unknown}\" type - the types are {string.Join(", ", EfHatchIssue.Types)}");

        // The scope, resolved once before the columns are walked. Null is the
        // whole board; a list is the subtree, and an empty one is a childless
        // issue, which is honestly "nothing under it" and falls out as an empty
        // queue and a 204.
        List<long>? scope = null;
        if (!string.IsNullOrWhiteSpace(ancestorKey))
        {
            if (!IssueKey.TryParse(ancestorKey, out var ancestorProject, out var ancestorNumber))
                return Scan.Refused($"there is no {ancestorKey}");

            var ancestorId = await db.Issues.WithKey(ancestorProject, ancestorNumber)
                .Select(i => (long?)i.Id).FirstOrDefaultAsync(ct);
            if (ancestorId is null) return Scan.Refused($"there is no {ancestorKey}");

            // The walk in Rollup, not a second one written here - a scope and a
            // meter that disagreed about what is under an epic would be a bug
            // nobody notices until the two are on the same screen.
            scope = await Rollup.DescendantIdsAsync(db, ancestorId.Value, ct);
        }

        var loop = new LoopScope(
            wanted,
            DayNumber(time.GetUtcNow(), offsetMinutes),
            offsetMinutes,
            await InFlightAsync(statuses, ct));

        var open = await Questions.OpenCountsAsync(db, ct);
        var playbooks = await db.Playbooks.AsNoTracking()
            .Include(p => p.FromStatus)
            .Include(p => p.ToStatus)
            .ToListAsync(ct);

        // The columns a pass looks in at all: the ones with a column to their
        // right. No type filter - which types the loop picks up is a reason a
        // row is folded, and the scan's job is to say so rather than to hide it.
        var walkable = statuses.Where(s => Advance(statuses, s) is not null).Select(s => s.Id).ToList();

        var query = db.Issues.Where(i => walkable.Contains(i.StatusId));
        if (scope is not null) query = query.Where(i => scope.Contains(i.Id));

        var candidates = await query
            .OrderBy(i => i.Rank).ThenBy(i => i.Id)
            .Include(i => i.Project)
            .ToListAsync(ct);

        var byColumn = candidates.GroupBy(i => i.StatusId).ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<ScanRow>();
        foreach (var status in Enumerable.Reverse(statuses))
        {
            if (Advance(statuses, status) is not { } to) continue;
            if (!byColumn.TryGetValue(status.Id, out var column)) continue;

            foreach (var issue in column)
            {
                var playbook = Match(playbooks, status.Id, to.Id, issue.Type);
                open.TryGetValue(issue.Id, out var waiting);
                rows.Add(new ScanRow(issue, status, to, Blocked(issue, status, to, playbook, waiting, loop)));
            }
        }

        return new Scan(statuses, rows, loop, null);
    }

    /// <summary>One issue the pass looked at, and what it decided.</summary>
    private sealed record ScanRow(EfHatchIssue Issue, EfHatchStatus From, EfHatchStatus? To, string? Blocked);

    /// <summary>
    /// A finished pass, or the argument it would not accept. A refusal carries
    /// the sentence and nothing else; both endpoints turn it into the same 400.
    /// </summary>
    private sealed record Scan(List<EfHatchStatus> Statuses, List<ScanRow> Rows, LoopScope? Loop, string? Failure)
    {
        public static Scan Refused(string why) => new([], [], null, why);
    }

    /// <summary>
    /// What the loop is asking of the board this pass: which types it may pick
    /// up, which day it is in the caller's zone, and what is already in flight.
    /// Absent when somebody named a ticket by hand - see <see cref="Blocked"/>.
    /// </summary>
    private sealed record LoopScope(string[] Wanted, long Today, int OffsetMinutes, List<InFlightIssue> InFlight);

    // ---- The loop's own policy ----
    //
    // Three rules that are not facts about an issue but decisions about what an
    // unattended run may start. They are asked in Blocked like every other
    // fold - a scan that could not name them would be a scan with holes in it -
    // but only when a LoopScope is present, which is to say only when the pass
    // is asking. A person who names a ticket is giving an instruction;
    // housekeeping does not overrule it.

    /// <summary>
    /// The types an unattended run picks up when the caller does not say.
    ///
    /// An epic is out because choosing what an effort contains is a product
    /// call, and a task because a task is a seam inside a story - the story is
    /// the unit that ships, and it carries its tasks across the board with it.
    /// Neither is a rule about the issue: both are still worked the moment
    /// somebody names one.
    /// </summary>
    private static readonly string[] LoopTypes = ["story", "bug"];

    /// <summary>
    /// The caller's type list, or <see cref="LoopTypes"/> when there is not
    /// one. False, with the offending name, for a type nobody defined - a
    /// misspelling that quietly matched nothing would read as a finished board.
    /// </summary>
    private static bool TryReadTypes(string? types, out string[] wanted, out string? unknown)
    {
        unknown = null;
        wanted = LoopTypes;
        if (string.IsNullOrWhiteSpace(types)) return true;

        var named = types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (named.Length == 0) return true;

        unknown = named.FirstOrDefault(t => !EfHatchIssue.Types.Contains(t, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null) return false;

        wanted = named.Select(t => t.ToLowerInvariant()).Distinct().ToArray();
        return true;
    }

    /// <summary>
    /// The last stop before shipped: the column immediately left of the first
    /// terminal one, or the rightmost column on a board with no terminal column
    /// at all.
    ///
    /// Measured rather than named, and measured the same way the <c>review</c>
    /// column was placed by the migration that added it
    /// (20260903204217_Playbooks.cs). An operator renames columns, and a
    /// hardcoded "review" would be a rule that quietly stopped applying.
    /// </summary>
    private static EfHatchStatus? AwaitingReview(List<EfHatchStatus> statuses)
    {
        var terminal = statuses.FindIndex(s => s.IsTerminal);
        var at = terminal < 0 ? statuses.Count - 1 : terminal - 1;
        return at >= 0 ? statuses[at] : null;
    }

    /// <summary>
    /// Every issue sitting in the awaiting-review column, with the parent it
    /// hangs under - one read, for a rule asked of every candidate.
    /// </summary>
    private async Task<List<InFlightIssue>> InFlightAsync(List<EfHatchStatus> statuses, CancellationToken ct)
    {
        if (AwaitingReview(statuses) is not { } awaiting) return [];

        // A null parent is not a group: two parentless issues are not siblings
        // of each other, so they are never loaded as one another's blocker.
        var rows = await db.Issues.AsNoTracking()
            .Where(i => i.StatusId == awaiting.Id && i.ParentId != null)
            .Select(i => new { i.Id, ParentId = i.ParentId!.Value, ProjectKey = i.Project!.Key, i.Number })
            .ToListAsync(ct);

        return rows
            .Select(r => new InFlightIssue(r.Id, r.ParentId, IssueKey.Format(r.ProjectKey, r.Number), awaiting.Name))
            .ToList();
    }

    /// <summary>
    /// Why this issue is not an unattended run's to start yet: a sibling of it
    /// is already awaiting review, and two open pull requests under one parent
    /// is one too many. Null when nothing under its parent is in flight.
    ///
    /// <para>The sentence is written here rather than at the caller even though
    /// <c>next</c> discards it - <c>next</c> folds in silence on purpose, and
    /// the scan that explains a whole pass reads the same fold and prints
    /// it.</para>
    /// </summary>
    private static string? SiblingInFlight(EfHatchIssue issue, List<InFlightIssue> inFlight)
    {
        if (issue.ParentId is not { } parent) return null;

        var sibling = inFlight.FirstOrDefault(o => o.ParentId == parent && o.Id != issue.Id);
        return sibling is null
            ? null
            : $"{sibling.Key} is in \"{sibling.Column}\", and two open pull requests under one parent is one too many";
    }

    /// <summary>An issue in the awaiting-review column, and what it takes to name it.</summary>
    private sealed record InFlightIssue(long Id, long ParentId, string Key, string Column);

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

        return await ResolveAsync(issue, await OrderedStatusesAsync(ct), null, ct);
    }

    // ---- Resolution ----

    /// <summary>
    /// One issue, rendered as a dispatch: the playbook, the children and the
    /// questions the spawned session is handed, on top of the same refusal the
    /// scan computed.
    /// </summary>
    /// <param name="loop">
    /// The pass this issue came out of, or null for an issue somebody named.
    /// Passed straight through to <see cref="Blocked"/> so that a row the scan
    /// called clear cannot come back blocked here.
    /// </param>
    private async Task<WorkDto> ResolveAsync(
        EfHatchIssue issue, List<EfHatchStatus> statuses, LoopScope? loop, CancellationToken ct)
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
            // The values the increment will actually run on, in place rather
            // than beside them. An issue's own model and effort beat whichever
            // playbook speaks for its next move, and a client that had to
            // remember to check a second pair of fields is a client that will
            // one day spawn sonnet on a ticket set to opus - silently. Nothing
            // is hidden by folding them in: issue.modelOverride rides the same
            // payload, and is how a printed line says where the value came
            // from. Everything else stays the matched row's own, so the
            // dispatch names the playbook that spoke *and* the values that won.
            playbook is null ? null : PlaybooksController.ToDto(playbook) with
            {
                Model = issue.ModelOverride ?? playbook.Model,
                Effort = issue.EffortOverride ?? playbook.Effort,
            },
            childCards,
            questions,
            Blocked(issue, from, to, playbook, waiting, loop));
    }

    /// <summary>
    /// Why an agent should not be spawned for this issue, or null when it
    /// should. Every fold in one place and in one order, so that the scan
    /// prints the same sentence the walk acted on.
    /// </summary>
    /// <remarks>
    /// <para>The order is what it costs to change the answer, most fundamental
    /// first. A column with nowhere an agent may go is a fact about the board
    /// and no argument alters it; the type is the caller's own argument; a
    /// ready date needs time; a question needs a person; a sibling needs other
    /// work to land; and a missing playbook needs the operator, which is last
    /// because it is only worth saying about an issue that is otherwise a
    /// candidate.</para>
    ///
    /// <para>Two of them are load-bearing rules rather than missing
    /// configuration: only the operator decides that something shipped, so a
    /// transition into a terminal column is not an agent's to make; and an
    /// issue holding an unanswered question is waiting on a person, so
    /// dispatching another agent at it would only produce a second session
    /// asking the same thing or guessing at the answer.</para>
    /// </remarks>
    /// <param name="waiting">
    /// Unanswered questions on the issue. Checked before the playbook, because
    /// "nobody has answered you" is a more useful sentence than "no playbook
    /// covers this" when both are true.
    /// </param>
    /// <param name="loop">
    /// The pass's own policy, or null when somebody named this ticket by hand.
    /// The three folds it adds are decisions about what an unattended run may
    /// *start*, as opposed to what may move, and a person who names a ticket is
    /// giving an instruction that housekeeping does not overrule.
    /// </param>
    private static string? Blocked(
        EfHatchIssue issue,
        EfHatchStatus from,
        EfHatchStatus? to,
        EfHatchPlaybook? playbook,
        int waiting,
        LoopScope? loop)
    {
        if (from.IsTerminal)
            return $"\"{from.Name}\" is where work ends - there is nothing after it";

        if (to is null)
            return $"there is no column after \"{from.Name}\", so there is nowhere for this to go";

        if (to.IsTerminal)
            return $"the next column is \"{to.Name}\", and only the operator moves work there";

        if (loop is not null)
        {
            if (!loop.Wanted.Contains(issue.Type))
                return $"{An(issue.Type)} is not a type an unattended run picks up";

            if (IsWaiting(issue, loop.Today, loop.OffsetMinutes))
                return $"not workable until {IssueMoment.Format(issue.ReadyAt, issue.ReadyAtHasTime)}";
        }

        if (waiting > 0)
            return $"{waiting} unanswered question{(waiting == 1 ? "" : "s")} - it is waiting on a person, not on an agent";

        if (loop is not null && SiblingInFlight(issue, loop.InFlight) is { } sibling)
            return sibling;

        return playbook is null
            ? $"no playbook covers \"{from.Name}\" to \"{to.Name}\" for {An(issue.Type)} - add one on the Playbooks page"
            : null;
    }

    /// <summary>
    /// A type with its article, because "a epic" in a sentence a person reads
    /// at a terminal is a typo they have to look past. The four types are
    /// enough for the crude rule to be the right one.
    /// </summary>
    private static string An(string type) =>
        "aeiou".Contains(char.ToLowerInvariant(type[0])) ? $"an {type}" : $"a {type}";

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
    private async Task<EfHatchPlaybook?> MatchAsync(int from, int to, string type, CancellationToken ct) =>
        Match(
            await db.Playbooks.AsNoTracking()
                .Include(p => p.FromStatus)
                .Include(p => p.ToStatus)
                .Where(p => p.FromStatusId == from && p.ToStatusId == to)
                .ToListAsync(ct),
            from, to, type);

    /// <summary>
    /// The same rule against rows already in hand, which is how a scan matches
    /// a whole board's worth of transitions without a query a row. The matrix
    /// is small enough to read whole and the tie-break is arithmetic.
    /// </summary>
    private static EfHatchPlaybook? Match(List<EfHatchPlaybook> playbooks, int from, int to, string type) =>
        playbooks
            .Where(p => p.FromStatusId == from && p.ToStatusId == to && p.Covers(type))
            .OrderByDescending(p => p.Specificity)
            .ThenBy(p => p.Id)
            .FirstOrDefault();

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
