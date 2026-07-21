using Aerie.Api.Ef;
using Aerie.Api.Services;
using HADotNet.Core.Clients;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Aerie.Api.Jobs;

/// <summary>
/// Replaces SampleEnvironments + SampleOutside (Phase 4 of
/// docs/device-architecture.md). Loads every enabled DeviceChannel, groups by
/// HaEntityId so each HA entity's history is fetched once regardless of how
/// many channels read from it (a Mysa thermostat's four channels all share
/// one entity), then writes one Measurement (numeric) or StateChange
/// (string, e.g. hvac_action) row per channel per historical state -
/// erasing the old climate./sensor.h5110 prefix-sniffing entirely.
/// </summary>
public class SampleChannels(TimeProvider t, HistoryClient haHistory, AerieContext db, IChannelHistoryWriter writer, ILogger<SampleChannels> logger) : IAerieJob
{
    public string Name => "SampleChannels";

    public string Group => "Aerie.Api";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("SampleChannels job execution started");

        var executionStart = t.GetUtcNow();
        var now = t.GetUtcNow();
        var then = now.Subtract(Interval * 2);
        logger.LogInformation("SampleChannels execution started for range {Then:O} to {Now:O}", then, now);

        var channelLoadStart = t.GetUtcNow();
        var channels = await db.DeviceChannels
            .AsNoTracking()
            .Include(c => c.Device)
            .Where(c => c.Device!.Enabled)
            .ToListAsync();
        var channelLoadTime = t.GetUtcNow() - channelLoadStart;
        logger.LogInformation("Loaded {ChannelCount} enabled channels in {ElapsedMs}ms", channels.Count, channelLoadTime.TotalMilliseconds);

        var groups = channels.GroupBy(c => c.HaEntityId).ToList();
        logger.LogInformation("Grouped into {GroupCount} HA entities", groups.Count);

        var historyFetchCount = 0;
        var measurementCount = 0;
        var stateChangeCount = 0;

        foreach (var group in groups)
        {
            var historyStart = t.GetUtcNow();
            var history = await haHistory.GetHistory(group.Key, then, now);
            var historyTime = t.GetUtcNow() - historyStart;

            if (history is null)
            {
                logger.LogInformation("No history found for entity {EntityId}", group.Key);
                continue;
            }

            historyFetchCount++;
            logger.LogInformation("Fetched {StateCount} states for entity {EntityId} in {ElapsedMs}ms", history.Count, group.Key, historyTime.TotalMilliseconds);

            var counts = await writer.WriteAsync(group.ToList(), history);
            measurementCount += counts.Measurements;
            stateChangeCount += counts.StateChanges;
        }

        var totalTime = t.GetUtcNow() - executionStart;
        logger.LogInformation("SampleChannels execution completed in {ElapsedMs}ms: fetched history for {HistoryCount} entities, inserted {MeasurementCount} measurements and {StateChangeCount} state changes",
            totalTime.TotalMilliseconds, historyFetchCount, measurementCount, stateChangeCount);
    }
}
