using Hatch.Api.Ef;
using Hatch.Api.Services;
using HADotNet.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hatch.Api.Tests;

/// <summary>
/// Covers the numeric/text routing, channel/state fan-out, and pre-existing-row
/// dedup in ChannelHistoryWriter.WriteAsync against an EF Core InMemory database.
/// Does NOT cover the concurrent-write race fallback (BulkInsert's catch on a
/// unique-constraint violation from a row inserted between the existing-rows
/// check and SaveChanges) - that relies on matching a real Npgsql
/// PostgresException, which InMemory doesn't raise, so it isn't reachable
/// here; that path would need a real Postgres (or Testcontainers) to exercise.
/// </summary>
public class ChannelHistoryWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ChannelHistoryWriter NewWriter(AppDbContext db) =>
        new(db, NullLogger<ChannelHistoryWriter>.Instance);

    private static EfDeviceChannel Channel(string? haAttribute) =>
        new() { Id = Guid.NewGuid(), HaEntityId = "sensor.test", HaAttribute = haAttribute };

    private static StateObject State(DateTimeOffset lastUpdated, string state, Dictionary<string, object>? attributes = null) =>
        new() { State = state, Attributes = attributes ?? [], LastUpdated = lastUpdated };

    [Fact]
    public async Task WriteAsync_InsertsMeasurement_ForNumericBareStateChannel()
    {
        await using var db = NewContext();
        var channel = Channel(haAttribute: null);
        var history = new HistoryList { State(Now, "70"), State(Now.AddMinutes(1), "71") };

        var counts = await NewWriter(db).WriteAsync([channel], history);

        Assert.Equal(2, counts.Measurements);
        Assert.Equal(0, counts.StateChanges);
        var rows = await db.Measurements.Where(m => m.ChannelId == channel.Id).OrderBy(m => m.Timestamp).ToListAsync();
        Assert.Equal([70m, 71m], rows.Select(r => r.Value));
        Assert.Equal(Now, rows[0].Timestamp);
    }

    [Fact]
    public async Task WriteAsync_InsertsStateChange_ForTextAttributeChannel()
    {
        await using var db = NewContext();
        var channel = Channel(haAttribute: "hvac_action");
        var history = new HistoryList
        {
            State(Now, "heat", new Dictionary<string, object> { ["hvac_action"] = "heating" }),
            State(Now.AddMinutes(1), "heat", new Dictionary<string, object> { ["hvac_action"] = "idle" }),
        };

        var counts = await NewWriter(db).WriteAsync([channel], history);

        Assert.Equal(0, counts.Measurements);
        Assert.Equal(2, counts.StateChanges);
        var rows = await db.StateChanges.Where(s => s.ChannelId == channel.Id).OrderBy(s => s.Timestamp).ToListAsync();
        Assert.Equal(["heating", "idle"], rows.Select(r => r.State));
    }

    [Fact]
    public async Task WriteAsync_SkipsStates_WithNoExtractableValue()
    {
        await using var db = NewContext();
        var channel = Channel(haAttribute: "current_temperature");
        var history = new HistoryList { State(Now, "heat", new Dictionary<string, object> { ["hvac_action"] = "heating" }) };

        var counts = await NewWriter(db).WriteAsync([channel], history);

        Assert.Equal(0, counts.Measurements);
        Assert.Equal(0, counts.StateChanges);
        Assert.False(await db.Measurements.AnyAsync());
        Assert.False(await db.StateChanges.AnyAsync());
    }

    [Fact]
    public async Task WriteAsync_WritesEachChannelSeparately_WhenMultipleChannelsShareHistory()
    {
        await using var db = NewContext();
        var tempChannel = Channel(haAttribute: "current_temperature");
        var hvacChannel = Channel(haAttribute: "hvac_action");
        var history = new HistoryList
        {
            State(Now, "heat", new Dictionary<string, object> { ["current_temperature"] = 68.5d, ["hvac_action"] = "heating" }),
        };

        var counts = await NewWriter(db).WriteAsync([tempChannel, hvacChannel], history);

        Assert.Equal(1, counts.Measurements);
        Assert.Equal(1, counts.StateChanges);
        Assert.Equal(68.5m, (await db.Measurements.SingleAsync()).Value);
        Assert.Equal(tempChannel.Id, (await db.Measurements.SingleAsync()).ChannelId);
        Assert.Equal("heating", (await db.StateChanges.SingleAsync()).State);
        Assert.Equal(hvacChannel.Id, (await db.StateChanges.SingleAsync()).ChannelId);
    }

    [Fact]
    public async Task WriteAsync_SkipsRows_AlreadyPresentInDatabase()
    {
        await using var db = NewContext();
        var channel = Channel(haAttribute: null);
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now, Value = 70m });
        await db.SaveChangesAsync();

        var history = new HistoryList { State(Now, "70"), State(Now.AddMinutes(1), "71") };

        var counts = await NewWriter(db).WriteAsync([channel], history);

        Assert.Equal(1, counts.Measurements);
        var rows = await db.Measurements.Where(m => m.ChannelId == channel.Id).OrderBy(m => m.Timestamp).ToListAsync();
        Assert.Equal([70m, 71m], rows.Select(r => r.Value));
    }

    [Fact]
    public async Task WriteAsync_DedupesWithinSameBatch_WhenHistoryHasRepeatedTimestamp()
    {
        await using var db = NewContext();
        var channel = Channel(haAttribute: null);
        var history = new HistoryList { State(Now, "70"), State(Now, "70") };

        var counts = await NewWriter(db).WriteAsync([channel], history);

        Assert.Equal(1, counts.Measurements);
        Assert.Equal(1, await db.Measurements.CountAsync());
    }
}
