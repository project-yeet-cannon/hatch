using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// What has to be said out loud when a ticket goes on the shelf.
///
/// <para>Deferring an issue is quiet by design - it leaves the board, and
/// nothing draws it any more. That is the whole point, and it is also the
/// hazard: an issue somebody else is waiting on stops being visible at exactly
/// the moment the waiting becomes permanent. A deferred blocker does not
/// satisfy a dependency (see <see cref="EfHatchIssueDependency.DependsOnId"/> -
/// only a terminal column does), because the code it was going to contain was
/// never written, and a story built on top of a branch that does not exist is
/// the failure the gate exists to prevent.</para>
///
/// <para>So the gate holds, and this writes the sentence that stops it holding
/// in silence: a comment on every issue that waits, on the ticket, where
/// somebody will find it. The alternative - letting the edge quietly become
/// permanent - is a ticket that never gets dispatched and no record anywhere
/// saying why.</para>
/// </summary>
public static class Deferrals
{
    /// <summary>
    /// Notes on every issue waiting on one of <paramref name="deferred"/> that
    /// what it waits on has been shelved.
    ///
    /// <para>Staged, not saved: the comments join whatever
    /// <c>SaveChanges</c> the move itself is part of, so a refused move leaves
    /// no note about a deferral that did not happen, and a bulk edit that
    /// shelves a whole subtree writes its notes in the same round trip.</para>
    /// </summary>
    /// <param name="deferred">
    /// The issues that have just entered a deferred column - and only those
    /// that just entered one. A ticket moved from one deferred column to
    /// another has not newly stopped, and its dependents have already been
    /// told; telling them again on every rename-shaped move is how a ticket
    /// ends up with nine identical comments nobody reads.
    /// </param>
    /// <param name="column">The deferred column they landed in, named in the sentence.</param>
    public static async Task NoteAsync(
        HatchContext db,
        IReadOnlyCollection<long> deferred,
        EfHatchStatus column,
        string actor,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (deferred.Count == 0) return;

        // The edges pointing at what was just shelved, read from the far end -
        // the direction EfHatchIssueDependency's second index exists for. The
        // dependent's own key is not needed: the comment lands on its row and
        // the sentence is about the blocker.
        var waiting = await db.Dependencies.AsNoTracking()
            .Where(d => deferred.Contains(d.DependsOnId))
            .Select(d => new
            {
                d.IssueId,
                BlockerProjectKey = d.DependsOn!.Project!.Key,
                BlockerNumber = d.DependsOn!.Number,
            })
            .ToListAsync(ct);

        foreach (var edge in waiting)
        {
            var blocker = IssueKey.Format(edge.BlockerProjectKey, edge.BlockerNumber);

            db.Comments.Add(new EfHatchComment
            {
                IssueId = edge.IssueId,
                Author = actor,
                Body =
                    $"{blocker} has been deferred to \"{column.Name}\". This issue depends on it, and a deferred "
                    + "blocker does not clear the gate - only work that lands does. So this stays where it is until "
                    + $"{blocker} comes back on the board, or until somebody drops the dependency.",
                CreatedAt = now,
            });
        }
    }
}
