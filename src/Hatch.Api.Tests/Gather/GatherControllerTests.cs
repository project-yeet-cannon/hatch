using Hatch.Api.Modules.Gather;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Hatch.Api.Tests.Gather;

/// <summary>
/// Covers the endpoints' own behaviour: that check and edit stay out of each
/// other's way, that a list is drawn in one response and in server order, and
/// that an item id only means anything on the list it belongs to.
/// </summary>
public class GatherControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The whole reason check/uncheck is its own endpoint. A phone checking
    /// things off in an aisle and the wall renaming one a second earlier is the
    /// collision that actually happens; the toggle carries no text, so it
    /// structurally cannot overwrite the rename with a stale copy.
    /// </summary>
    [Fact]
    public async Task Check_LeavesTheTextAlone()
    {
        var h = await NewHarnessAsync();
        var item = await h.AddAsync("Milk", "2 gal", "the blue cap");

        var checkedItem = Value(await h.Controller.CheckItem(h.List.Id, item.Id, default));

        Assert.True(checkedItem.IsChecked);
        Assert.Equal("Milk", checkedItem.Name);
        Assert.Equal("2 gal", checkedItem.Quantity);
        Assert.Equal("the blue cap", checkedItem.Note);
        Assert.Equal(1, await h.Db.Items.CountAsync());
    }

    [Fact]
    public async Task Uncheck_ClearsWhenItWasChecked_AndStillLeavesTheTextAlone()
    {
        var h = await NewHarnessAsync();
        var item = await h.AddAsync("Milk", "2 gal", "the blue cap");
        var checkedAt = Value(await h.Controller.CheckItem(h.List.Id, item.Id, default)).CheckedAt;

        var cleared = Value(await h.Controller.UncheckItem(h.List.Id, item.Id, default));

        Assert.NotNull(checkedAt);
        Assert.False(cleared.IsChecked);
        Assert.Null(cleared.CheckedAt);
        Assert.Equal("2 gal", cleared.Quantity);
        Assert.Equal("the blue cap", cleared.Note);
    }

    /// <summary>And the other half of the bargain: an edit doesn't un-check anything.</summary>
    [Fact]
    public async Task UpdateItem_LeavesTheCheckAlone()
    {
        var h = await NewHarnessAsync();
        var item = await h.AddAsync("Milk");
        await h.Controller.CheckItem(h.List.Id, item.Id, default);

        var edited = Value(await h.Controller.UpdateItem(h.List.Id, item.Id, new ItemWriteRequest("Oat milk", "1 qt", null), default));

        Assert.True(edited.IsChecked);
        Assert.Equal("Oat milk", edited.Name);
    }

    [Fact]
    public async Task UpdateItem_RefusesToRenameOntoSomethingAlreadyOnTheList()
    {
        var h = await NewHarnessAsync();
        await h.AddAsync("Milk");
        var eggs = await h.AddAsync("Eggs");

        // Merging silently would lose whatever was on one of the two rows.
        var result = await h.Controller.UpdateItem(h.List.Id, eggs.Id, new ItemWriteRequest("  milk ", null, null), default);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateItem_AllowsARenameThatOnlyChangesTheSpelling()
    {
        var h = await NewHarnessAsync();
        var item = await h.AddAsync("milk");

        var edited = Value(await h.Controller.UpdateItem(h.List.Id, item.Id, new ItemWriteRequest("Milk", null, null), default));

        Assert.Equal("Milk", edited.Name);
    }

    [Fact]
    public async Task UpdateItem_ClearsAQuantity_WhenTheEditOmitsIt()
    {
        var h = await NewHarnessAsync();
        var item = await h.AddAsync("Milk", "2 gal");

        // The edit overwrites where the add merges - it is how a wrong quantity
        // gets taken off at all.
        var edited = Value(await h.Controller.UpdateItem(h.List.Id, item.Id, new ItemWriteRequest("Milk", null, null), default));

        Assert.Null(edited.Quantity);
    }

    [Fact]
    public async Task GetList_DrawsTheWholeListInOneResponse_InServerOrder()
    {
        var h = await NewHarnessAsync();
        await h.AddAsync("Milk");
        var eggs = await h.AddAsync("Eggs");
        await h.AddAsync("Bread");
        await h.Controller.CheckItem(h.List.Id, eggs.Id, default);

        var detail = Value(await h.Controller.GetList(h.List.Id, default));

        // Still to get, oldest first; then the cart, most recently checked first.
        Assert.Equal(["Milk", "Bread", "Eggs"], detail.Items.Select(i => i.Name));
        Assert.Equal(2, detail.List.OpenCount);
        Assert.Equal(1, detail.List.CheckedCount);
    }

    [Fact]
    public async Task GetLists_CountsWhatIsLeftToGetSeparatelyFromWhatIsInTheCart()
    {
        var h = await NewHarnessAsync();
        var milk = await h.AddAsync("Milk");
        await h.AddAsync("Eggs");
        await h.Controller.CheckItem(h.List.Id, milk.Id, default);

        var summary = Assert.Single(await h.Controller.GetLists(default));

        Assert.Equal(1, summary.OpenCount);
        Assert.Equal(1, summary.CheckedCount);
    }

    /// <summary>
    /// Item routes are scoped by list, so an id from another list is a 404
    /// rather than an edit that reaches across.
    /// </summary>
    [Fact]
    public async Task ItemEndpoints_DoNotReachIntoAnotherList()
    {
        var h = await NewHarnessAsync();
        var hardware = new GatherList { Name = "Hardware", CreatedAt = Now, UpdatedAt = Now };
        h.Db.Lists.Add(hardware);
        await h.Db.SaveChangesAsync();
        var screws = Value(await h.Controller.AddItem(hardware.Id, new ItemAddRequest("Screws", null, null), default));

        Assert.IsType<NotFoundResult>((await h.Controller.CheckItem(h.List.Id, screws.Id, default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.UpdateItem(h.List.Id, screws.Id, new ItemWriteRequest("Nails", null, null), default)).Result);
        Assert.IsType<NotFoundResult>(await h.Controller.DeleteItem(h.List.Id, screws.Id, default));
    }

    [Fact]
    public async Task ClearChecked_ReportsWhatItRemoved()
    {
        var h = await NewHarnessAsync();
        var milk = await h.AddAsync("Milk");
        await h.AddAsync("Eggs");
        await h.Controller.CheckItem(h.List.Id, milk.Id, default);

        var result = Value(await h.Controller.ClearChecked(h.List.Id, default));

        Assert.Equal(1, result.Deleted);
        Assert.Equal(["Eggs"], Value(await h.Controller.GetList(h.List.Id, default)).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task EndpointsOnAListThatDoesNotExist_Are404_NotAnEmptyList()
    {
        var h = await NewHarnessAsync();
        var missing = Guid.NewGuid();

        Assert.IsType<NotFoundResult>((await h.Controller.GetList(missing, default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.AddItem(missing, new ItemAddRequest("Milk", null, null), default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.ClearChecked(missing, default)).Result);
        Assert.IsType<NotFoundResult>((await h.Controller.UpdateList(missing, new ListWriteRequest("Grocery", null, null), default)).Result);
        Assert.IsType<NotFoundResult>(await h.Controller.DeleteList(missing, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddItem_RefusesABlankName(string name)
    {
        var h = await NewHarnessAsync();

        Assert.IsType<BadRequestObjectResult>((await h.Controller.AddItem(h.List.Id, new ItemAddRequest(name, null, null), default)).Result);
    }

    /// <summary>
    /// Length is answered rather than left to the column, so over-long text is a
    /// 400 that says which field, not a 500 out of a truncation error.
    /// </summary>
    [Fact]
    public async Task AddItem_RefusesTextLongerThanTheColumn()
    {
        var h = await NewHarnessAsync();

        Assert.IsType<BadRequestObjectResult>(
            (await h.Controller.AddItem(h.List.Id, new ItemAddRequest(new string('x', GatherItem.MaxNameLength + 1), null, null), default)).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await h.Controller.AddItem(h.List.Id, new ItemAddRequest("Milk", new string('x', GatherItem.MaxQuantityLength + 1), null), default)).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await h.Controller.AddItem(h.List.Id, new ItemAddRequest("Milk", null, new string('x', GatherItem.MaxNoteLength + 1)), default)).Result);
    }

    [Fact]
    public async Task CreateList_TrimsAndKeepsTheIconAndColour_AndStartsEmpty()
    {
        var h = await NewHarnessAsync();

        var created = (CreatedAtActionResult)(await h.Controller.CreateList(new ListWriteRequest("  Hardware ", "*", "amber"), default)).Result!;
        var summary = (ListSummaryDto)created.Value!;

        Assert.Equal("Hardware", summary.Name);
        Assert.Equal("*", summary.Icon);
        Assert.Equal("amber", summary.Color);
        Assert.Equal(0, summary.OpenCount);
    }

    [Fact]
    public async Task CreateList_RefusesABlankName()
    {
        var h = await NewHarnessAsync();

        Assert.IsType<BadRequestObjectResult>((await h.Controller.CreateList(new ListWriteRequest("  ", null, null), default)).Result);
    }

    [Fact]
    public async Task DeleteList_TakesItsItemsWithIt()
    {
        var h = await NewHarnessAsync();
        await h.AddAsync("Milk");

        Assert.IsType<NoContentResult>(await h.Controller.DeleteList(h.List.Id, default));
        Assert.Empty(await h.Db.Items.ToListAsync());
    }

    private static async Task<Harness> NewHarnessAsync()
    {
        var db = new GatherContext(
            new DbContextOptionsBuilder<GatherContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var time = new FakeTimeProvider(Now);
        var list = new GatherList { Name = "Grocery", CreatedAt = Now, UpdatedAt = Now };
        db.Lists.Add(list);
        await db.SaveChangesAsync();

        var controller = new GatherController(db, new GatherService(db, time, NullLogger<GatherService>.Instance), time);
        return new Harness(controller, db, list, time);
    }

    private static T Value<T>(ActionResult<T> result) =>
        result.Value ?? throw new InvalidOperationException($"expected a value, got {result.Result?.GetType().Name ?? "nothing"}");

    /// <summary>One controller over one empty "Grocery" list, plus the clock driving it.</summary>
    private sealed record Harness(GatherController Controller, GatherContext Db, GatherList List, FakeTimeProvider Time)
    {
        /// <summary>
        /// Adds to the harness list and then moves the clock on, so CreatedAt
        /// orders the items the way they went in - ties would otherwise fall
        /// through to Id, which is random.
        /// </summary>
        public async Task<ItemDto> AddAsync(string name, string? quantity = null, string? note = null)
        {
            var item = Value(await Controller.AddItem(List.Id, new ItemAddRequest(name, quantity, note), default));
            Time.Advance(TimeSpan.FromMinutes(1));
            return item;
        }
    }
}
