using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// What a subtree has cost: the work log, added up through the hierarchy.
///
/// Its own file rather than a method on <see cref="Rollup"/>, and the reason is
/// that the rule is the opposite one. A status rollup counts <em>leaves</em>,
/// because a parent sitting in review whose tasks are all in todo has not done
/// the tasks' work and counting its own column would be counting it twice.
/// Spend is not like that: a session run against an epic is money no child
/// holds, so the total is plain addition - this issue's own entries plus every
/// descendant's, at any depth.
///
/// Two rules that look alike and are not is exactly the kind of thing that gets
/// quietly unified by a later refactor, so they sit in two files and each says
/// why.
/// </summary>
public static class WorkLogRollup
{
    /// <summary>
    /// One issue's totals, with or without everything beneath it.
    /// </summary>
    /// <remarks>
    /// "Below this issue" is <see cref="Rollup.DescendantIdsAsync"/>, called and
    /// not redefined: two definitions of descendant is the divergence nobody
    /// notices until a filter and a meter disagree about the same epic.
    ///
    /// One aggregate query over <c>IssueId IN (self ∪ descendants)</c>, so a
    /// deep epic costs one round trip rather than one per story. An issue with
    /// nothing beneath it and nothing on it totals zero rather than erroring -
    /// which is most issues, most of the time.
    /// </remarks>
    public static async Task<WorkLogTotalsDto> TotalsAsync(
        HatchContext db, long issueId, bool includeDescendants, CancellationToken ct)
    {
        var ids = new List<long> { issueId };
        if (includeDescendants) ids.AddRange(await Rollup.DescendantIdsAsync(db, issueId, ct));

        // Summed in memory rather than by the database, which is the one place
        // this differs from what a production-sized table would want and is the
        // same trade every other fold in the module makes: EF's in-memory
        // provider does not implement Sum over a decimal projection the way
        // Npgsql does, and a work log is one row per increment.
        var rows = await db.WorkLog.AsNoTracking()
            .Where(w => ids.Contains(w.IssueId))
            .Select(w => new
            {
                w.IsError,
                w.InputTokens,
                w.OutputTokens,
                w.CacheCreationTokens,
                w.CacheReadTokens,
                w.CostUsd,
            })
            .ToListAsync(ct);

        var input = rows.Sum(r => r.InputTokens);
        var output = rows.Sum(r => r.OutputTokens);
        var cacheCreation = rows.Sum(r => r.CacheCreationTokens);
        var cacheRead = rows.Sum(r => r.CacheReadTokens);

        return new WorkLogTotalsDto(
            rows.Count,
            rows.Count(r => r.IsError),
            input,
            output,
            cacheCreation,
            cacheRead,
            // The headline, added up here so it has one definition - the same
            // reason WorkLogEntryDto carries its own rather than leaving it to
            // the client.
            input + output + cacheCreation + cacheRead,
            // An errored session's spend counts. It ran, and it was billed for
            // running.
            rows.Sum(r => r.CostUsd));
    }
}
