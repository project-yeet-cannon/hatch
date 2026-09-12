namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// Where a column sits on the board, answered once for everybody who asks.
///
/// Every rule here is measured off the board's own shape rather than off a
/// column's name, because an operator renames columns and a hardcoded "review"
/// is a rule that quietly stops applying. That reasoning was written for the
/// dispatcher, but it is not the dispatcher's alone: <see cref="WorkController"/>
/// decides what an unattended run may start, and <see cref="AttentionController"/>
/// decides which issues are waiting on a person to look at them, and the two
/// must not come to different answers about which column is which.
///
/// <para>This file holds that the way <see cref="Questions"/> holds the one
/// definition of "open" - a shared static rather than a private helper copied
/// into a second controller.</para>
/// </summary>
public static class Columns
{
    /// <summary>
    /// The columns the board actually draws, in order: every status that is not
    /// deferred.
    ///
    /// <para>Every measurement below is taken off this list rather than off the
    /// status table, because a deferred column is not a lane work passes
    /// through - it is a siding. Counting one would move the board's landmarks
    /// by one the moment somebody ticked a box on the Statuses page: a
    /// <c>Parked</c> column sorted between review and done would make
    /// <see cref="AwaitingReview"/> name it, and the attention panel would go
    /// looking for pull requests in a column nothing is dispatched from.</para>
    ///
    /// <para>Sort order is the operator's, so a deferred column can sit
    /// anywhere in it; the board simply closes over the gap.</para>
    /// </summary>
    public static List<EfHatchStatus> Board(List<EfHatchStatus> statuses) =>
        statuses.Where(s => !s.IsDeferred).ToList();

    /// <summary>
    /// The column immediately to the right, or null at the end of the board.
    /// Terminal columns are returned rather than skipped - a caller refusing a
    /// move wants to name the one it is refusing.
    ///
    /// <para>Deferred columns are skipped on the way past and have nothing
    /// after them: work is never advanced into a siding, and nothing advances
    /// out of one either. A ticket comes back off the shelf because a person
    /// put it back, which is a press on the issue page and not a step a pass
    /// takes.</para>
    /// </summary>
    public static EfHatchStatus? Advance(List<EfHatchStatus> statuses, EfHatchStatus from)
    {
        if (from.IsTerminal || from.IsDeferred) return null;

        var board = Board(statuses);
        var at = board.FindIndex(s => s.Id == from.Id);
        return at >= 0 && at + 1 < board.Count ? board[at + 1] : null;
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
    ///
    /// <para>It is a landmark as well as a column: <see cref="Implementation"/>
    /// is measured from it, and the attention panel's pull request half is the
    /// issues standing in it.</para>
    /// </summary>
    public static EfHatchStatus? AwaitingReview(List<EfHatchStatus> statuses)
    {
        var board = Board(statuses);
        var terminal = board.FindIndex(s => s.IsTerminal);
        var at = terminal < 0 ? board.Count - 1 : terminal - 1;
        return at >= 0 ? board[at] : null;
    }

    /// <summary>
    /// The column where an agent writes the code: the one whose own next move
    /// is into the awaiting-review column. Null on a board too short to have
    /// one, where a dependency therefore gates nothing.
    /// </summary>
    /// <remarks>
    /// On a stock board that is "In Progress", and the gated move is "To Do" to
    /// "In Progress" - the one transition where code gets written. Measured and
    /// not named, for the reason <see cref="AwaitingReview"/> is: an operator
    /// renames columns, and a hardcoded name is a rule that quietly stops
    /// applying.
    /// </remarks>
    public static EfHatchStatus? Implementation(List<EfHatchStatus> statuses) =>
        AwaitingReview(statuses) is { } review
            ? statuses.FirstOrDefault(s => Advance(statuses, s)?.Id == review.Id)
            : null;
}
