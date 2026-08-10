using Aerie.Api.Modules.Storage;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Tests.Storage;

/// <summary>
/// Covers the two delete behaviours the storage model turns on, because they're
/// the difference between losing a shelf and losing the record of everything
/// that was on it. Configuration lives in StorageContext.OnModelCreating; the
/// migration carries the matching SET NULL / CASCADE onto the real FKs.
/// </summary>
public class StorageContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeletingACrate_TakesItsItems_AndLeavesItsLocation()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid crateId, locationId;

        await using (var db = NewContext(dbName))
        {
            var location = new StorageLocation { Name = "Garage", CreatedAt = Now };
            var crate = new Crate { Code = "ABC123", Location = location, CreatedAt = Now, UpdatedAt = Now };
            crate.Items.Add(new Item { Name = "Drill", CreatedAt = Now, UpdatedAt = Now });
            crate.Items.Add(new Item { Name = "Bits", CreatedAt = Now, UpdatedAt = Now });
            db.Crates.Add(crate);
            await db.SaveChangesAsync();
            (crateId, locationId) = (crate.Id, location.Id);
        }

        await using (var db = NewContext(dbName))
        {
            var crate = await db.Crates.Include(c => c.Items).FirstAsync(c => c.Id == crateId);
            db.Crates.Remove(crate);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbName))
        {
            Assert.Empty(await db.Items.ToListAsync());
            Assert.True(await db.Locations.AnyAsync(l => l.Id == locationId));
        }
    }

    [Fact]
    public async Task DeletingALocation_OrphansItsCrates_RatherThanDeletingThem()
    {
        var dbName = Guid.NewGuid().ToString();
        Guid locationId;

        await using (var db = NewContext(dbName))
        {
            var location = new StorageLocation { Name = "Attic", CreatedAt = Now };
            location.Crates.Add(new Crate { Code = "ABC123", CreatedAt = Now, UpdatedAt = Now });
            db.Locations.Add(location);
            await db.SaveChangesAsync();
            locationId = location.Id;
        }

        await using (var db = NewContext(dbName))
        {
            var location = await db.Locations.Include(l => l.Crates).FirstAsync(l => l.Id == locationId);
            db.Locations.Remove(location);
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext(dbName))
        {
            var crate = await db.Crates.SingleAsync();
            Assert.Equal("ABC123", crate.Code);
            Assert.Null(crate.LocationId);
        }
    }

    private static StorageContext NewContext(string dbName) =>
        new(new DbContextOptionsBuilder<StorageContext>().UseInMemoryDatabase(dbName).Options);
}
