using System.Globalization;
using Hatch.Api.Ef;
using Hatch.Api.Services;
using HADotNet.Core.Clients;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Hatch.Api.Jobs;

/// <summary>
/// On-demand counterpart to SampleChannels: pulls an arbitrary HA history
/// range for one Device's channels, for backfilling a fresh Hatch install
/// (or a newly-added device) from existing HA history. Registered as a
/// durable job with no trigger (see JobsInit.WireUpTriggerableJob) - it only
/// runs when DevicesController fires it via IScheduler.TriggerJob with a
/// deviceId/from/to JobDataMap, never on a schedule.
/// </summary>
public class BackfillChannelHistory(HistoryClient haHistory, AppDbContext db, IChannelHistoryWriter writer, ILogger<BackfillChannelHistory> logger) : IJob
{
    public const string Name = "BackfillChannelHistory";
    public const string Group = "Hatch.Api";

    public async Task Execute(IJobExecutionContext context)
    {
        var map = context.MergedJobDataMap;
        var deviceId = Guid.Parse(map.GetString("deviceId")!);
        var from = DateTimeOffset.Parse(map.GetString("from")!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var to = DateTimeOffset.Parse(map.GetString("to")!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        logger.LogInformation("BackfillChannelHistory started for device {DeviceId}, range {From:O} to {To:O}", deviceId, from, to);
        var executionStart = DateTimeOffset.UtcNow;

        var channels = await db.DeviceChannels
            .AsNoTracking()
            .Where(c => c.DeviceId == deviceId)
            .ToListAsync();

        if (channels.Count == 0)
        {
            logger.LogWarning("BackfillChannelHistory found no channels for device {DeviceId}; nothing to do", deviceId);
            return;
        }

        var groups = channels.GroupBy(c => c.HaEntityId).ToList();
        logger.LogInformation("Grouped {ChannelCount} channels into {GroupCount} HA entities for device {DeviceId}", channels.Count, groups.Count, deviceId);

        var historyFetchCount = 0;
        var measurementCount = 0;
        var stateChangeCount = 0;

        foreach (var group in groups)
        {
            var historyStart = DateTimeOffset.UtcNow;
            var history = await haHistory.GetHistory(group.Key, from, to);
            var historyTime = DateTimeOffset.UtcNow - historyStart;

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

        var totalTime = DateTimeOffset.UtcNow - executionStart;
        logger.LogInformation(
            "BackfillChannelHistory completed for device {DeviceId} in {ElapsedMs}ms: fetched history for {HistoryCount} entities, inserted {MeasurementCount} measurements and {StateChangeCount} state changes",
            deviceId, totalTime.TotalMilliseconds, historyFetchCount, measurementCount, stateChangeCount);
    }
}
