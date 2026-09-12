using Hatch.Api.Modules.Gather;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.Gather;

/// <summary>
/// Covers the delete behaviour the Gather model turns on. Configuration lives
/// in GatherContext.OnModelCreating; the migration carries the matching CASCADE
/// onto the real FK.
/// </summary>
public class GatherContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeletingAList_TakesItsItems_AndLeavesEveryOtherListAlone()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid groceryId, hardwareId;

        await using (var db = NewContext(dbName))
        {
            var grocery = NewList("Grocery", "Milk", "Eggs");
            var hardware = NewList("Hardware", "Screws");
            db.Lists.AddRange(grocery, hardware);
            await db.SaveChangesAsync();
            (groceryId, hardwareId) = (grocery.Id, hardware.Id);
        }

        await using (var db = NewContext(dbName))
        {
            var grocery = await db.Lists.Include(l => l.Items).FirstAsync(l => l.Id == groceryId);
            db.Lists.Remove(grocery);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbName))
        {
            // An item has no meaning without the list it's on - there is no
            // "where is it" question left to answer, so it goes with the list.
            var item = await db.Items.SingleAsync();
            Assert.Equal("Screws", item.Name);
            Assert.Equal(hardwareId, item.ListId);
        }
    }

    [Fact]
    public async Task DeletingAnItem_LeavesTheList()
    {
        var dbName = Guid.NewGuid().ToString();

        await using (var db = NewContext(dbName))
        {
            db.Lists.Add(NewList("Grocery", "Milk"));
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbName))
        {
            db.Items.Remove(await db.Items.SingleAsync());
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbName))
        {
            Assert.Equal("Grocery", (await db.Lists.SingleAsync()).Name);
        }
    }

    private static GatherList NewList(string name, params string[] items) => new()
    {
        Name = name,
        CreatedAt = Now,
        UpdatedAt = Now,
        Items = [.. items.Select(i => new GatherItem { Name = i, CreatedAt = Now, UpdatedAt = Now })],
    };

    private static GatherContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<GatherContext>().UseInMemoryDatabase(dbName).Options);
}
