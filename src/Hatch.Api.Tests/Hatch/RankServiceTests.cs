using Hatch.Api.Modules.Hatch;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.Hatch;

/// <summary>
/// The ordering arithmetic, on its own. It is worth this many tests because it
/// is the one piece of Hatch that fails invisibly: a rank that lands on a
/// neighbour does not throw, it just quietly puts a card back where it was, and
/// the operator drags it again and blames the mouse.
/// </summary>
public class RankServiceTests
{
    [Fact]
    public async Task TheFirstCardInAnEmptyColumn_TakesZero()
    {
        var h = await NewAsync();

        Assert.Equal(0, await h.Ranks.BottomAsync(h.Todo, default));
    }

    [Fact]
    public async Task EachNewCard_LandsAGapBelowTheLastOne()
    {
        var h = await NewAsync();
        await h.PlaceAsync(h.Todo, 0);
        await h.PlaceAsync(h.Todo, RankService.Gap);

        Assert.Equal(2 * RankService.Gap, await h.Ranks.BottomAsync(h.Todo, default));
    }

    [Fact]
    public async Task ACardDroppedBetweenTwo_TakesTheMidpoint()
    {
        var h = await NewAsync();
        var top = await h.PlaceAsync(h.Todo, 0);
        var bottom = await h.PlaceAsync(h.Todo, RankService.Gap);

        var rank = await h.Ranks.PlaceAsync(h.Todo, null, top.Id, bottom.Id, default);

        Assert.Equal(512, rank);
    }

    [Fact]
    public async Task ACardDroppedAtTheTop_GoesAGapAboveTheFirstOne()
    {
        var h = await NewAsync();
        var first = await h.PlaceAsync(h.Todo, 0);

        var rank = await h.Ranks.PlaceAsync(h.Todo, null, null, first.Id, default);

        // Negative is a perfectly good rank - the column is ordered, not
        // numbered from anywhere in particular.
        Assert.Equal(-RankService.Gap, rank);
    }

    /// <summary>
    /// The client sends the neighbour it can see. A board that refetched a
    /// moment late names a card that has since moved, and the honest answer is
    /// the bottom of the column rather than a refused drag.
    /// </summary>
    [Fact]
    public async Task ANeighbourThatIsNoLongerInThisColumn_LandsTheCardAtTheBottom()
    {
        var h = await NewAsync();
        await h.PlaceAsync(h.Todo, 0);
        var elsewhere = await h.PlaceAsync(h.Done, 0);

        var rank = await h.Ranks.PlaceAsync(h.Todo, null, elsewhere.Id, null, default);

        Assert.Equal(RankService.Gap, rank);
    }

    /// <summary>
    /// Two cards can share a rank - nothing forbids it, and an import that
    /// writes a column in one go could. The id breaks the tie so the column has
    /// a stable order, and a card dropped between them finds no midpoint and
    /// renumbers, which is the same answer as any other exhausted gap.
    /// </summary>
    [Fact]
    public async Task CardsSharingARank_AreOrderedByIdAndSeparatedByARenumber()
    {
        var h = await NewAsync();
        var a = await h.PlaceAsync(h.Todo, 500);
        var b = await h.PlaceAsync(h.Todo, 500);

        var between = await h.Ranks.PlaceAsync(h.Todo, null, a.Id, b.Id, default);

        Assert.Equal(0, a.Rank);
        Assert.Equal(RankService.Gap, b.Rank);
        Assert.InRange(between, a.Rank + 1, b.Rank - 1);
    }

    // ---- Exhaustion ----

    [Fact]
    public async Task WhenTwoCardsAreAdjacentIntegers_TheColumnIsRenumberedAndTheCardStillFits()
    {
        var h = await NewAsync();
        var above = await h.PlaceAsync(h.Todo, 5);
        var below = await h.PlaceAsync(h.Todo, 6);

        var rank = await h.Ranks.PlaceAsync(h.Todo, null, above.Id, below.Id, default);

        // Renumbered to 0 and 1024, so the midpoint exists again.
        Assert.Equal(0, above.Rank);
        Assert.Equal(RankService.Gap, below.Rank);
        Assert.Equal(512, rank);
    }

    [Fact]
    public async Task ARenumberedColumn_KeepsTheOrderItAlreadyHad()
    {
        var h = await NewAsync();
        var first = await h.PlaceAsync(h.Todo, -900);
        var second = await h.PlaceAsync(h.Todo, 5);
        var third = await h.PlaceAsync(h.Todo, 6);
        var fourth = await h.PlaceAsync(h.Todo, 7000);

        var rank = await h.Ranks.PlaceAsync(h.Todo, null, second.Id, third.Id, default);
        await h.Db.SaveChangesAsync();

        Assert.Equal([0, RankService.Gap, 2 * RankService.Gap, 3 * RankService.Gap],
            new[] { first.Rank, second.Rank, third.Rank, fourth.Rank });
        Assert.InRange(rank, second.Rank + 1, third.Rank - 1);
    }

    /// <summary>
    /// A renumber must not save on its own. The rewritten column and the card
    /// that caused it belong to one <c>SaveChanges</c>, or a failure between
    /// them leaves a column with two cards on the same rank.
    /// </summary>
    [Fact]
    public async Task ARenumber_IsLeftForTheCallerToSave()
    {
        var h = await NewAsync();
        var above = await h.PlaceAsync(h.Todo, 5);
        await h.PlaceAsync(h.Todo, 6);
        h.Db.ChangeTracker.Clear();

        await h.Ranks.PlaceAsync(h.Todo, null, above.Id, null, default);

        var unsaved = await h.Db.Issues.AsNoTracking().SingleAsync(i => i.Id == above.Id);
        Assert.Equal(5, unsaved.Rank);
    }

    // ---- Moving a card that is already in the column ----

    /// <summary>
    /// A card cannot be its own neighbour. Left in the column it would occupy
    /// one of the two positions the midpoint is being taken between, and the
    /// card would land on top of itself and appear not to have moved.
    /// </summary>
    [Fact]
    public async Task ACardMovingWithinItsColumn_IsNotItsOwnNeighbour()
    {
        var h = await NewAsync();
        var top = await h.PlaceAsync(h.Todo, 0);
        var middle = await h.PlaceAsync(h.Todo, RankService.Gap);
        var bottom = await h.PlaceAsync(h.Todo, 2 * RankService.Gap);

        // Drag the top card down between the other two.
        var rank = await h.Ranks.PlaceAsync(h.Todo, top.Id, middle.Id, bottom.Id, default);

        Assert.InRange(rank, middle.Rank + 1, bottom.Rank - 1);
    }

    [Fact]
    public async Task ACardDraggedToTheBottomOfItsOwnColumn_EndsUpBelowEverythingElse()
    {
        var h = await NewAsync();
        var top = await h.PlaceAsync(h.Todo, 0);
        var bottom = await h.PlaceAsync(h.Todo, RankService.Gap);

        var rank = await h.Ranks.PlaceAsync(h.Todo, top.Id, bottom.Id, null, default);

        Assert.True(rank > bottom.Rank);
    }

    // ---- Columns are independent ----

    [Fact]
    public async Task RanksAreOnlyEverComparedWithinAColumn()
    {
        var h = await NewAsync();
        await h.PlaceAsync(h.Done, 999_999);

        Assert.Equal(0, await h.Ranks.BottomAsync(h.Todo, default));
    }

    // ---- Harness ----

    private sealed class Harness
    {
        public required HatchContext Db { get; init; }
        public required RankService Ranks { get; init; }
        public required int ProjectId { get; init; }
        public required int Todo { get; init; }
        public required int Done { get; init; }

        private int number;

        /// <summary>Puts a card in a column at an exact rank, which is what these tests need and no endpoint offers.</summary>
        public async Task<EfHatchIssue> PlaceAsync(int statusId, long rank)
        {
            var issue = new EfHatchIssue
            {
                ProjectId = ProjectId,
                Number = ++number,
                Type = "task",
                Title = $"card {number}",
                StatusId = statusId,
                Rank = rank,
                CreatedBy = "operator",
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            Db.Issues.Add(issue);
            await Db.SaveChangesAsync();
            return issue;
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static async Task<Harness> NewAsync()
    {
        var db = new HatchContext(
            new DbContextOptionsBuilder<HatchContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var project = new EfHatchProject { Key = "AER", Name = "Hatch", CreatedAt = Now };
        var todo = new EfHatchStatus { Name = "todo", SortOrder = 20 };
        var done = new EfHatchStatus { Name = "done", SortOrder = 40, IsTerminal = true };
        db.AddRange(project, todo, done);
        await db.SaveChangesAsync();

        return new Harness
        {
            Db = db,
            Ranks = new RankService(db),
            ProjectId = project.Id,
            Todo = todo.Id,
            Done = done.Id,
        };
    }
}
