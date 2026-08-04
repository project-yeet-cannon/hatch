using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>
/// Covers ChannelLatestValues.GetLatestAsync's per-channel latest-row lookups
/// against an EF Core InMemory database - same convention as ChannelHistoryWriterTests.
/// </summary>
public class ChannelLatestValuesTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static AerieContext NewContext() =>
        new(new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task GetLatestAsync_ReturnsNewestMeasurement_ForNumericOnlyChannel()
    {
        await using var db = NewContext();
        var channelId = Guid.NewGuid();
        db.Measurements.AddRange(
            new EfMeasurement { ChannelId = channelId, Timestamp = Now, Value = 70m },
            new EfMeasurement { ChannelId = channelId, Timestamp = Now.AddMinutes(5), Value = 72m });
        await db.SaveChangesAsync();

        var latest = await ChannelLatestValues.GetLatestAsync(db, [channelId], CancellationToken.None);

        Assert.Equal(72m, latest[channelId].Value);
        Assert.Null(latest[channelId].State);
        Assert.Equal(Now.AddMinutes(5), latest[channelId].Timestamp);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsNewestStateChange_ForTextOnlyChannel()
    {
        await using var db = NewContext();
        var channelId = Guid.NewGuid();
        db.StateChanges.AddRange(
            new EfStateChange { ChannelId = channelId, Timestamp = Now, State = "idle" },
            new EfStateChange { ChannelId = channelId, Timestamp = Now.AddMinutes(5), State = "heating" });
        await db.SaveChangesAsync();

        var latest = await ChannelLatestValues.GetLatestAsync(db, [channelId], CancellationToken.None);

        Assert.Equal("heating", latest[channelId].State);
        Assert.Null(latest[channelId].Value);
        Assert.Equal(Now.AddMinutes(5), latest[channelId].Timestamp);
    }

    [Fact]
    public async Task GetLatestAsync_PicksNewerMeasurement_WhenMeasurementIsMostRecent()
    {
        await using var db = NewContext();
        var channelId = Guid.NewGuid();
        db.StateChanges.Add(new EfStateChange { ChannelId = channelId, Timestamp = Now, State = "idle" });
        db.Measurements.Add(new EfMeasurement { ChannelId = channelId, Timestamp = Now.AddMinutes(5), Value = 72m });
        await db.SaveChangesAsync();

        var latest = await ChannelLatestValues.GetLatestAsync(db, [channelId], CancellationToken.None);

        Assert.Equal(72m, latest[channelId].Value);
        Assert.Null(latest[channelId].State);
        Assert.Equal(Now.AddMinutes(5), latest[channelId].Timestamp);
    }

    [Fact]
    public async Task GetLatestAsync_PicksNewerStateChange_WhenStateChangeIsMostRecent()
    {
        await using var db = NewContext();
        var channelId = Guid.NewGuid();
        db.Measurements.Add(new EfMeasurement { ChannelId = channelId, Timestamp = Now, Value = 72m });
        db.StateChanges.Add(new EfStateChange { ChannelId = channelId, Timestamp = Now.AddMinutes(5), State = "heating" });
        await db.SaveChangesAsync();

        var latest = await ChannelLatestValues.GetLatestAsync(db, [channelId], CancellationToken.None);

        Assert.Equal("heating", latest[channelId].State);
        Assert.Null(latest[channelId].Value);
        Assert.Equal(Now.AddMinutes(5), latest[channelId].Timestamp);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsAllNull_ForChannelWithNoSamples()
    {
        await using var db = NewContext();
        var channelId = Guid.NewGuid();

        var latest = await ChannelLatestValues.GetLatestAsync(db, [channelId], CancellationToken.None);

        Assert.True(latest.ContainsKey(channelId));
        Assert.Null(latest[channelId].Value);
        Assert.Null(latest[channelId].State);
        Assert.Null(latest[channelId].Timestamp);
    }

    [Fact]
    public async Task GetLatestAsync_DoesNotCrossContaminate_AcrossMultipleChannels()
    {
        await using var db = NewContext();
        var channelA = Guid.NewGuid();
        var channelB = Guid.NewGuid();
        db.Measurements.AddRange(
            new EfMeasurement { ChannelId = channelA, Timestamp = Now, Value = 70m },
            new EfMeasurement { ChannelId = channelB, Timestamp = Now.AddMinutes(10), Value = 55m });
        await db.SaveChangesAsync();

        var latest = await ChannelLatestValues.GetLatestAsync(db, [channelA, channelB], CancellationToken.None);

        Assert.Equal(70m, latest[channelA].Value);
        Assert.Equal(55m, latest[channelB].Value);
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsEmpty_ForEmptyChannelIdCollection()
    {
        await using var db = NewContext();

        var latest = await ChannelLatestValues.GetLatestAsync(db, [], CancellationToken.None);

        Assert.Empty(latest);
    }
}
