using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// Where a card sits in its column, as a number.
///
/// Ordering by a sparse integer is what makes a drag one UPDATE instead of a
/// rewritten column: the ranks start 1024 apart, and a card dropped between two
/// takes the midpoint. Lexorank strings were considered and rejected for this
/// board (docs/hatch.md, "Ordering") - string midpoint arithmetic has sharp
/// edges around exhausted alphabets, and a column here holds tens of cards, so
/// the case those strings exist to avoid is a single cheap UPDATE.
/// </summary>
/// <remarks>
/// The client never sends a rank. It says "after this card, before that one"
/// and this decides the number, which keeps every client dumb in the same way -
/// the board, a script, and Claude all move a card by naming its neighbours.
/// </remarks>
public class RankService(HatchContext db)
{
    /// <summary>
    /// The space left between two neighbouring cards. Large enough that a
    /// column is halved ten times before the gaps close, small enough that
    /// nothing here goes near the ends of a <c>long</c>.
    /// </summary>
    public const long Gap = 1024;

    /// <summary>
    /// The rank for a card landing in <paramref name="statusId"/> between two
    /// neighbours - either of which may be absent, meaning the top or the
    /// bottom of the column.
    ///
    /// Mutates tracked entities when the column has to be renumbered and never
    /// saves: the caller's single <c>SaveChangesAsync</c> commits the renumber
    /// and the placed card together, so a column cannot be left half-rewritten.
    /// </summary>
    /// <param name="movingIssueId">
    /// The card being placed, when it is already in this column - excluded from
    /// the neighbour maths and from a renumber, so a card is never asked to sit
    /// between two positions it is itself occupying.
    /// </param>
    public async Task<long> PlaceAsync(
        int statusId, long? movingIssueId, long? afterIssueId, long? beforeIssueId, CancellationToken ct)
    {
        var column = await db.Issues
            .Where(i => i.StatusId == statusId && (movingIssueId == null || i.Id != movingIssueId))
            .OrderBy(i => i.Rank)
            .ThenBy(i => i.Id)
            .ToListAsync(ct);

        var index = InsertionIndex(column, afterIssueId, beforeIssueId);

        var rank = Midpoint(column, index);
        if (rank is not null) return rank.Value;

        // The gaps in this column have closed. Rewriting it costs one UPDATE
        // per card in a column that holds tens of them, and it happens roughly
        // once per ten halvings of the same gap - which is why the simple
        // integer rank is affordable at all.
        Renumber(column);
        return Midpoint(column, index)
            ?? throw new InvalidOperationException("a renumbered column still had no midpoint");
    }

    /// <summary>
    /// The rank for a card appended to the bottom of a column - what a freshly
    /// created issue takes.
    /// </summary>
    public Task<long> BottomAsync(int statusId, CancellationToken ct) =>
        PlaceAsync(statusId, null, null, null, ct);

    /// <summary>
    /// Where in the ordered column the card lands. The client sends both
    /// neighbours when it has both, and this trusts whichever it was given -
    /// deriving the position from one of them rather than requiring the pair to
    /// agree, because a board that refetched a moment late would otherwise turn
    /// a stale neighbour into a refused drop.
    /// </summary>
    private static int InsertionIndex(List<EfHatchIssue> column, long? afterIssueId, long? beforeIssueId)
    {
        if (beforeIssueId is { } before)
        {
            var at = column.FindIndex(i => i.Id == before);
            if (at >= 0) return at;
        }

        if (afterIssueId is { } after)
        {
            var at = column.FindIndex(i => i.Id == after);
            if (at >= 0) return at + 1;
        }

        // No neighbours, or neighbours that have since moved out of this
        // column: the bottom is the honest place for a card whose requested
        // position no longer exists.
        return column.Count;
    }

    /// <summary>
    /// A rank strictly between the cards on either side of
    /// <paramref name="index"/>, or null when the two are adjacent integers and
    /// there is nothing left between them.
    /// </summary>
    private static long? Midpoint(List<EfHatchIssue> column, int index)
    {
        var below = index > 0 ? column[index - 1].Rank : (long?)null;
        var above = index < column.Count ? column[index].Rank : (long?)null;

        return (below, above) switch
        {
            (null, null) => 0,              // the first card in an empty column
            (null, { } a) => a - Gap,       // the new top; negatives are fine
            ({ } b, null) => b + Gap,       // the new bottom
            // Computed as an offset rather than as (b + a) / 2 so the sum
            // cannot overflow and so truncation cannot land on a neighbour.
            ({ } b, { } a) => a - b > 1 ? b + (a - b) / 2 : null,
        };
    }

    /// <summary>Rewrites the column to 0, 1024, 2048… keeping the order it already had.</summary>
    private static void Renumber(List<EfHatchIssue> column)
    {
        for (var i = 0; i < column.Count; i++) column[i].Rank = i * Gap;
    }
}
