using Hatch.Api.Ef;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.DeviceMapping;

/// <summary>Covers ChannelHistoryQuery.GetAsync against an EF Core InMemory database.</summary>
public class ChannelHistoryQueryTests
{
    private static readonly DateTimeOffset From = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(1);

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EfDeviceChannel Channel(DeviceChannelMetric metric) =>
        new() { Id = Guid.NewGuid(), HaEntityId = "sensor.test", Metric = metric };

    [Fact]
    public async Task GetAsync_ReturnsBucketedPoints_ForNumericChannel()
    {
        await using var db = NewContext();
        var channel = Channel(DeviceChannelMetric.Temperature);
        db.Measurements.AddRange(
            new EfMeasurement { ChannelId = channel.Id, Timestamp = From.AddMinutes(5), Value = 70m },
            new EfMeasurement { ChannelId = channel.Id, Timestamp = From.AddMinutes(10), Value = 72m });
        await db.SaveChangesAsync();

        var result = await ChannelHistoryQuery.GetAsync(db, [channel], From, To, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(channel.Id, result[0].ChannelId);
        Assert.Single(result[0].Points);
        Assert.Equal(71m, result[0].Points[0].Value);
        Assert.Empty(result[0].States);
    }

    [Fact]
    public async Task GetAsync_ReturnsRawStates_ForTextChannel()
    {
        await using var db = NewContext();
        var channel = Channel(DeviceChannelMetric.HvacAction);
        db.StateChanges.AddRange(
            new EfStateChange { ChannelId = channel.Id, Timestamp = From.AddMinutes(5), State = "idle" },
            new EfStateChange { ChannelId = channel.Id, Timestamp = From.AddMinutes(10), State = "heating" });
        await db.SaveChangesAsync();

        var result = await ChannelHistoryQuery.GetAsync(db, [channel], From, To, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Single(result);
        Assert.Empty(result[0].Points);
        Assert.Equal(2, result[0].States.Count);
        Assert.Equal(["idle", "heating"], result[0].States.Select(s => s.State));
    }

    [Fact]
    public async Task GetAsync_DoesNotCrossContaminate_AcrossMultipleChannels()
    {
        await using var db = NewContext();
        var tempChannel = Channel(DeviceChannelMetric.Temperature);
        var hvacChannel = Channel(DeviceChannelMetric.HvacAction);
        db.Measurements.Add(new EfMeasurement { ChannelId = tempChannel.Id, Timestamp = From.AddMinutes(5), Value = 70m });
        db.StateChanges.Add(new EfStateChange { ChannelId = hvacChannel.Id, Timestamp = From.AddMinutes(5), State = "heating" });
        await db.SaveChangesAsync();

        var result = await ChannelHistoryQuery.GetAsync(db, [tempChannel, hvacChannel], From, To, TimeSpan.FromMinutes(30), CancellationToken.None);

        var tempResult = result.Single(r => r.ChannelId == tempChannel.Id);
        var hvacResult = result.Single(r => r.ChannelId == hvacChannel.Id);
        Assert.Single(tempResult.Points);
        Assert.Empty(tempResult.States);
        Assert.Empty(hvacResult.Points);
        Assert.Single(hvacResult.States);
    }

    [Fact]
    public async Task GetAsync_ExcludesRows_OutsideWindow()
    {
        await using var db = NewContext();
        var channel = Channel(DeviceChannelMetric.Temperature);
        db.Measurements.AddRange(
            new EfMeasurement { ChannelId = channel.Id, Timestamp = From.AddMinutes(-5), Value = 60m },
            new EfMeasurement { ChannelId = channel.Id, Timestamp = From.AddMinutes(5), Value = 70m },
            new EfMeasurement { ChannelId = channel.Id, Timestamp = To.AddMinutes(5), Value = 80m });
        await db.SaveChangesAsync();

        var result = await ChannelHistoryQuery.GetAsync(db, [channel], From, To, TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Single(result[0].Points);
        Assert.Equal(70m, result[0].Points[0].Value);
    }
}
