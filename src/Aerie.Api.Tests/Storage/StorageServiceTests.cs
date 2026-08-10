using Aerie.Api.Modules.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Storage;

/// <summary>
/// Covers crate minting: batch creation and the code-collision redraw, against
/// an EF Core InMemory database.
/// </summary>
/// <remarks>
/// InMemory doesn't enforce the unique index, so these exercise the pre-insert
/// check - the path that actually keeps codes unique in practice. The
/// DbUpdateException retry behind it is a race-only safety net that needs a real
/// Postgres to provoke, and is deliberately not faked here.
/// </remarks>
public class StorageServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateCratesAsync_MintsTheRequestedCount_WithDistinctCodes()
    {
        await using var db = NewContext();
        var service = NewService(db);

        var crates = await service.CreateCratesAsync(25, initial: null, CancellationToken.None);

        Assert.Equal(25, crates.Count);
        Assert.Equal(25, crates.Select(c => c.Code).Distinct().Count());
        Assert.Equal(25, await db.Crates.CountAsync());
        Assert.All(crates, c => Assert.Equal(CrateCode.Length, c.Code.Length));
    }

    [Fact]
    public async Task CreateCratesAsync_LeavesBatchCratesUnlabelled_AndTimestamped()
    {
        await using var db = NewContext();

        var crates = await NewService(db).CreateCratesAsync(3, initial: null, CancellationToken.None);

        // A batch crate is a blank box with tape on it; naming happens at fill time.
        Assert.All(crates, c =>
        {
            Assert.Null(c.Label);
            Assert.Null(c.LocationId);
            Assert.Equal(Now, c.CreatedAt);
            Assert.Equal(Now, c.UpdatedAt);
        });
    }

    [Fact]
    public async Task CreateCratesAsync_AppliesInitialValues_ForSingleCrateCreation()
    {
        await using var db = NewContext();
        var location = new StorageLocation { Name = "Attic", CreatedAt = Now };
        db.Locations.Add(location);
        await db.SaveChangesAsync();

        var crate = (await NewService(db).CreateCratesAsync(
            1, new CrateWriteRequest("Christmas decorations", location.Id, "top shelf"), CancellationToken.None))[0];

        Assert.Equal("Christmas decorations", crate.Label);
        Assert.Equal(location.Id, crate.LocationId);
        Assert.Equal("top shelf", crate.Notes);
    }

    [Fact]
    public async Task CreateCratesAsync_RedrawsACode_AlreadyTakenByAnotherCrate()
    {
        await using var db = NewContext();
        db.Crates.Add(new Crate { Code = "ABC123", CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync();

        // The RNG hands out the taken code first, so the draw has to notice and retry.
        var crate = (await NewService(db, "ABC123", "XYZ789").CreateCratesAsync(1, initial: null, CancellationToken.None))[0];

        Assert.Equal("XYZ789", crate.Code);
        Assert.Equal(2, await db.Crates.CountAsync());
    }

    [Fact]
    public async Task CreateCratesAsync_RedrawsADuplicate_DrawnTwiceWithinOneBatch()
    {
        await using var db = NewContext();

        var crates = await NewService(db, "AAA111", "AAA111", "BBB222")
            .CreateCratesAsync(2, initial: null, CancellationToken.None);

        Assert.Equal(["AAA111", "BBB222"], crates.Select(c => c.Code).Order());
    }

    [Fact]
    public async Task CreateCratesAsync_GivesUp_RatherThanSpinning_WhenEveryDrawIsTaken()
    {
        await using var db = NewContext();
        db.Crates.Add(new Crate { Code = "ABC123", CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync();

        // A source that can only ever return a code that's already taken.
        var stuck = new FakeCrateCodeSource(Enumerable.Repeat("ABC123", 1000).ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(db, stuck).CreateCratesAsync(1, initial: null, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(StorageService.MaxBatchCount + 1)]
    public async Task CreateCratesAsync_RejectsCountsOutsideTheBatchRange(int count)
    {
        await using var db = NewContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => NewService(db).CreateCratesAsync(count, initial: null, CancellationToken.None));
    }

    private static StorageContext NewContext() =>
        new(new DbContextOptionsBuilder<StorageContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static StorageService NewService(StorageContext db, params string[] scriptedCodes) =>
        NewService(db, new FakeCrateCodeSource(scriptedCodes));

    private static StorageService NewService(StorageContext db, ICrateCodeSource codes) =>
        new(db, codes, new FakeTimeProvider(Now), NullLogger<StorageService>.Instance);

    /// <summary>Hands out scripted codes in order, then falls back to real random ones.</summary>
    private sealed class FakeCrateCodeSource(params string[] scripted) : ICrateCodeSource
    {
        private readonly Queue<string> queue = new(scripted);

        public string Next() => queue.Count > 0 ? queue.Dequeue() : CrateCode.Next();
    }
}
