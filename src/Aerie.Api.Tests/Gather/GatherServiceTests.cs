using Aerie.Api.Modules.Gather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Gather;

/// <summary>
/// Covers the two operations that aren't CRUD: the add upsert and the
/// clear-checked sweep, against an EF Core InMemory database.
/// </summary>
/// <remarks>
/// InMemory doesn't enforce the unique index, so these exercise the lookup -
/// the path that actually collapses a re-add in practice. The
/// DbUpdateException retry behind it is a race-only safety net that needs a
/// real Postgres to provoke, and is deliberately not faked here.
/// </remarks>
public class GatherServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddItem_AddsTheItem_WhenTheListDoesNotHaveIt()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);

        var item = await NewService(db).AddItemAsync(list.Id, new ItemAddRequest(" Whole Milk ", "2 gal", "the blue cap"), default);

        Assert.Equal("Whole Milk", item.Name);
        Assert.Equal("whole milk", item.NameNormalized);
        Assert.Equal("2 gal", item.Quantity);
        Assert.Equal("the blue cap", item.Note);
        Assert.False(item.IsChecked);
        Assert.Null(item.CheckedAt);
        Assert.Equal(Now, item.CreatedAt);
        Assert.Equal(1, await db.Items.CountAsync());
    }

    [Fact]
    public async Task AddItem_ReturnsTheItemAlreadyThere_RatherThanASecondRow()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var service = NewService(db);
        var first = await service.AddItemAsync(list.Id, new ItemAddRequest("Milk", null, null), default);

        // Someone at the kiosk who can't see the list types it again, differently.
        var second = await service.AddItemAsync(list.Id, new ItemAddRequest("  MILK  ", null, null), default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Items.CountAsync());
        // The spelling already on the list wins: a second person writing it in
        // caps is not a rename, and the row shouldn't flicker between the two.
        Assert.Equal("Milk", second.Name);
    }

    [Fact]
    public async Task AddItem_UnchecksAnItemThatWasAlreadyBought()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var service = NewService(db);
        var item = await service.AddItemAsync(list.Id, new ItemAddRequest("Milk", null, null), default);
        item.IsChecked = true;
        item.CheckedAt = Now;
        await db.SaveChangesAsync();

        // Re-adding a checked item is someone putting it back on the list. The
        // row that says it was already bought is the stale one.
        var readded = await service.AddItemAsync(list.Id, new ItemAddRequest("milk", null, null), default);

        Assert.Equal(item.Id, readded.Id);
        Assert.False(readded.IsChecked);
        Assert.Null(readded.CheckedAt);
    }

    [Fact]
    public async Task AddItem_MergesQuantityAndNote_RatherThanErasingThem()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var service = NewService(db);
        await service.AddItemAsync(list.Id, new ItemAddRequest("Milk", "2 gal", "the blue cap"), default);

        // A bare re-add carries no quantity - which is silence, not "none".
        var merged = await service.AddItemAsync(list.Id, new ItemAddRequest("milk", null, null), default);

        Assert.Equal("2 gal", merged.Quantity);
        Assert.Equal("the blue cap", merged.Note);
    }

    [Fact]
    public async Task AddItem_TakesAQuantityTheReAddActuallyCarried()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var service = NewService(db);
        await service.AddItemAsync(list.Id, new ItemAddRequest("Milk", "2 gal", null), default);

        var updated = await service.AddItemAsync(list.Id, new ItemAddRequest("milk", " 3 gal ", null), default);

        Assert.Equal("3 gal", updated.Quantity);
    }

    [Fact]
    public async Task AddItem_KeepsListsApart_SoTwoStoresCanBothWantMilk()
    {
        await using var db = NewContext();
        var grocery = await NewListAsync(db, "Grocery");
        var warehouse = await NewListAsync(db, "Warehouse");
        var service = NewService(db);

        var a = await service.AddItemAsync(grocery.Id, new ItemAddRequest("Milk", null, null), default);
        var b = await service.AddItemAsync(warehouse.Id, new ItemAddRequest("Milk", null, null), default);

        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, await db.Items.CountAsync());
    }

    [Fact]
    public async Task AddItem_MarksTheListTouched_SoLastActivityMeansIt()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var time = new FakeTimeProvider(Now);
        time.Advance(TimeSpan.FromHours(3));

        await NewService(db, time).AddItemAsync(list.Id, new ItemAddRequest("Milk", null, null), default);

        Assert.Equal(Now.AddHours(3), (await db.Lists.FirstAsync()).UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddItem_RefusesANameThatNormalizesToNothing(string name)
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => NewService(db).AddItemAsync(list.Id, new ItemAddRequest(name, null, null), default));
    }

    [Fact]
    public async Task ClearChecked_RemovesWhatIsInTheCart_AndReportsHowMany()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var service = NewService(db);
        foreach (var name in new[] { "Milk", "Eggs", "Bread" })
        {
            var item = await service.AddItemAsync(list.Id, new ItemAddRequest(name, null, null), default);
            if (name != "Bread") item.IsChecked = true;
        }
        await db.SaveChangesAsync();

        var deleted = await service.ClearCheckedAsync(list.Id, default);

        Assert.Equal(2, deleted);
        Assert.Equal("Bread", (await db.Items.SingleAsync()).Name);
    }

    [Fact]
    public async Task ClearChecked_IsZero_AndLeavesTheListAlone_WhenNothingIsChecked()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var service = NewService(db);
        await service.AddItemAsync(list.Id, new ItemAddRequest("Milk", null, null), default);

        Assert.Equal(0, await service.ClearCheckedAsync(list.Id, default));
        Assert.Equal(1, await db.Items.CountAsync());
    }

    [Fact]
    public async Task ClearChecked_OnlySweepsTheListItWasAskedAbout()
    {
        await using var db = NewContext();
        var grocery = await NewListAsync(db, "Grocery");
        var hardware = await NewListAsync(db, "Hardware");
        var service = NewService(db);
        foreach (var (list, name) in new[] { (grocery, "Milk"), (hardware, "Screws") })
        {
            (await service.AddItemAsync(list.Id, new ItemAddRequest(name, null, null), default)).IsChecked = true;
        }
        await db.SaveChangesAsync();

        Assert.Equal(1, await service.ClearCheckedAsync(grocery.Id, default));
        Assert.Equal("Screws", (await db.Items.SingleAsync()).Name);
    }

    /// <summary>
    /// The order both clients draw, so neither has to decide it: what's left to
    /// get, oldest first, then what's already in the cart, most recent first.
    /// </summary>
    [Fact]
    public async Task InDisplayOrder_PutsOpenItemsFirst_AndTheLastThingCheckedAtTheTopOfTheRest()
    {
        await using var db = NewContext();
        var list = await NewListAsync(db);
        var time = new FakeTimeProvider(Now);
        var service = NewService(db, time);

        var names = new[] { "Milk", "Eggs", "Bread", "Jam" };
        var added = new List<GatherItem>();
        foreach (var name in names)
        {
            added.Add(await service.AddItemAsync(list.Id, new ItemAddRequest(name, null, null), default));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        // Milk went in the cart first, then Bread - so Bread reads at the top of
        // the checked block, where it's easy to un-cross if it was a mistake.
        added[0].IsChecked = true;
        added[0].CheckedAt = Now;
        added[2].IsChecked = true;
        added[2].CheckedAt = Now.AddMinutes(30);
        await db.SaveChangesAsync();

        var ordered = await GatherService.InDisplayOrder(db.Items.AsNoTracking().Where(i => i.ListId == list.Id))
            .Select(i => i.Name)
            .ToListAsync();

        Assert.Equal(["Eggs", "Jam", "Bread", "Milk"], ordered);
    }

    private static GatherContext NewContext() =>
        new(new DbContextOptionsBuilder<GatherContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<GatherList> NewListAsync(GatherContext db, string name = "Grocery")
    {
        var list = new GatherList { Name = name, CreatedAt = Now, UpdatedAt = Now };
        db.Lists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    private static GatherService NewService(GatherContext db) => NewService(db, new FakeTimeProvider(Now));

    private static GatherService NewService(GatherContext db, TimeProvider time) =>
        new(db, time, NullLogger<GatherService>.Instance);
}
