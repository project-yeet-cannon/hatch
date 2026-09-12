using Hatch.Api.Ef;
using Hatch.Api.Services.Routines;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.Routines;

/// <summary>Covers RoutineService.GetRoutinesAsync's Included/SortOrder filtering against an EF Core InMemory database.</summary>
public class RoutineServiceTests
{
    private static IDbContextFactory<AppDbContext> NewFactory() =>
        new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task GetRoutinesAsync_ExcludesRoutines_WhereIncludedIsFalse()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.AddRange(
                new EfRoutine { Id = Guid.NewGuid(), Name = "Night mode", SortOrder = 0, Included = true },
                new EfRoutine { Id = Guid.NewGuid(), Name = "Hidden", SortOrder = 1, Included = false });
            await db.SaveChangesAsync();
        }

        var result = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Night mode", result[0].Name);
    }

    [Fact]
    public async Task GetRoutinesAsync_OrdersBySortOrder_ThenName()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.AddRange(
                new EfRoutine { Id = Guid.NewGuid(), Name = "Max AC", SortOrder = 1, Included = true },
                new EfRoutine { Id = Guid.NewGuid(), Name = "Night mode", SortOrder = 0, Included = true },
                new EfRoutine { Id = Guid.NewGuid(), Name = "Arrival", SortOrder = 1, Included = true });
            await db.SaveChangesAsync();
        }

        var result = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.Equal(["Night mode", "Arrival", "Max AC"], result.Select(r => r.Name));
    }

    [Fact]
    public async Task GetRoutinesAsync_MapsDescription_IncludingNull()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(new EfRoutine { Id = Guid.NewGuid(), Name = "Night mode", Description = "Dims the house", SortOrder = 0, Included = true });
            await db.SaveChangesAsync();
        }

        var result = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.Equal("Dims the house", result[0].Description);
    }

    [Fact]
    public async Task GetRoutinesAsync_NonToggleRoutine_HasNullIsActive()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(new EfRoutine { Id = Guid.NewGuid(), Name = "Night mode", SortOrder = 0, Included = true, IsToggle = false });
            await db.SaveChangesAsync();
        }

        var result = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.False(result[0].IsToggle);
        Assert.Null(result[0].IsActive);
    }

    [Fact]
    public async Task GetRoutinesAsync_ToggleRoutine_IsActiveWhenEveryPowerChannelIsOn()
    {
        var factory = NewFactory();
        var lightA = Guid.NewGuid();
        var lightB = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(new EfRoutine
            {
                Id = Guid.NewGuid(),
                Name = "Outdoor floodlights",
                SortOrder = 0,
                Included = true,
                IsToggle = true,
                Actions =
                [
                    new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = lightA, Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 0 },
                    new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = lightB, Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 1 },
                ],
            });
            db.StateChanges.AddRange(
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = lightA, State = "on", Timestamp = DateTimeOffset.UtcNow },
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = lightB, State = "on", Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var result = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.True(result[0].IsToggle);
        Assert.True(result[0].IsActive);
    }

    [Fact]
    public async Task GetRoutinesAsync_ToggleRoutine_IsInactiveWhenAnyPowerChannelIsOff()
    {
        var factory = NewFactory();
        var lightA = Guid.NewGuid();
        var lightB = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(new EfRoutine
            {
                Id = Guid.NewGuid(),
                Name = "Outdoor floodlights",
                SortOrder = 0,
                Included = true,
                IsToggle = true,
                Actions =
                [
                    new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = lightA, Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 0 },
                    new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = lightB, Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 1 },
                ],
            });
            db.StateChanges.AddRange(
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = lightA, State = "on", Timestamp = DateTimeOffset.UtcNow },
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = lightB, State = "off", Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var result = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.False(result[0].IsActive);
    }

    [Fact]
    public async Task GetRoutinesAsync_ToggleRoutine_IsInactiveWhenNoSamplesYet()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(new EfRoutine
            {
                Id = Guid.NewGuid(),
                Name = "Outdoor floodlights",
                SortOrder = 0,
                Included = true,
                IsToggle = true,
                Actions = [new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid(), Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 0 }],
            });
            await db.SaveChangesAsync();
        }

        var result = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.False(result[0].IsActive);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext(options));
    }
}
