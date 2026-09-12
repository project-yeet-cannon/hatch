using Hatch.Api.Ef;
using HADotNet.Core.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Hatch.Api.Services;

/// <summary>Counts written by a single WriteAsync call, for the caller's own log line.</summary>
public record ChannelWriteCounts(int Measurements, int StateChanges);

/// <summary>
/// Writes a HA entity's historical states into Measurement/StateChange rows
/// for the channels that read from it. Shared between SampleChannels (recent
/// history on a recurring schedule) and BackfillChannelHistory (an arbitrary
/// range, triggered on demand) - the channel/state -> row logic is identical,
/// only where the history comes from and how often differs.
/// </summary>
public interface IChannelHistoryWriter
{
    Task<ChannelWriteCounts> WriteAsync(IReadOnlyCollection<EfDeviceChannel> channels, HistoryList history);
}

public class ChannelHistoryWriter(AppDbContext db, ILogger<ChannelHistoryWriter> logger) : IChannelHistoryWriter
{
    /// <summary>Bounds retries on the rare race where a row is inserted concurrently between our existing-rows check and SaveChanges.</summary>
    private const int MaxAttempts = 3;

    public async Task<ChannelWriteCounts> WriteAsync(IReadOnlyCollection<EfDeviceChannel> channels, HistoryList history)
    {
        var measurements = new List<EfMeasurement>();
        var stateChanges = new List<EfStateChange>();

        foreach (var channel in channels)
        {
            foreach (var state in history)
            {
                var (numeric, text) = ChannelValueExtractor.Extract(state, channel);
                if (numeric is decimal value)
                {
                    measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = state.LastUpdated, Value = value });
                }
                else if (text is not null)
                {
                    stateChanges.Add(new EfStateChange { ChannelId = channel.Id, Timestamp = state.LastUpdated, State = text });
                }
            }
        }

        // Dedupe candidates against each other (same channel/timestamp pair seen twice in this
        // batch) before touching the DB, since AddRange-ing both copies would violate the unique
        // index within the same SaveChanges call.
        measurements = measurements.DistinctBy(m => (m.ChannelId, m.Timestamp)).ToList();
        stateChanges = stateChanges.DistinctBy(s => (s.ChannelId, s.Timestamp)).ToList();

        var channelIds = channels.Select(c => c.Id).ToArray();
        var timestamps = history.Select(s => s.LastUpdated).ToArray();

        var newMeasurements = await ExcludeExisting(measurements, channelIds, timestamps);
        var newStateChanges = await ExcludeExisting(stateChanges, channelIds, timestamps);

        await BulkInsert(newMeasurements, newStateChanges);

        return new ChannelWriteCounts(newMeasurements.Count, newStateChanges.Count);
    }

    private async Task<List<EfMeasurement>> ExcludeExisting(List<EfMeasurement> candidates, Guid[] channelIds, DateTimeOffset[] timestamps)
    {
        if (candidates.Count == 0) return candidates;

        var existing = await db.Measurements.AsNoTracking()
            .Where(m => channelIds.Contains(m.ChannelId) && timestamps.Contains(m.Timestamp))
            .Select(m => new { m.ChannelId, m.Timestamp })
            .ToListAsync();
        var existingKeys = existing.Select(m => (m.ChannelId, m.Timestamp)).ToHashSet();

        return candidates.Where(m => !existingKeys.Contains((m.ChannelId, m.Timestamp))).ToList();
    }

    private async Task<List<EfStateChange>> ExcludeExisting(List<EfStateChange> candidates, Guid[] channelIds, DateTimeOffset[] timestamps)
    {
        if (candidates.Count == 0) return candidates;

        var existing = await db.StateChanges.AsNoTracking()
            .Where(s => channelIds.Contains(s.ChannelId) && timestamps.Contains(s.Timestamp))
            .Select(s => new { s.ChannelId, s.Timestamp })
            .ToListAsync();
        var existingKeys = existing.Select(s => (s.ChannelId, s.Timestamp)).ToHashSet();

        return candidates.Where(s => !existingKeys.Contains((s.ChannelId, s.Timestamp))).ToList();
    }

    /// <summary>
    /// Inserts both lists in a single SaveChanges call (Npgsql batches consecutive inserts of the
    /// same entity type into multi-row statements). The pre-filtering in ExcludeExisting normally
    /// means nothing collides here; on the rare race where a concurrent writer beats us to a row,
    /// we drop the tracked entities that now exist and retry the remainder instead of failing the
    /// whole batch over one duplicate.
    /// </summary>
    private async Task BulkInsert(List<EfMeasurement> measurements, List<EfStateChange> stateChanges)
    {
        for (var attempt = 1; measurements.Count > 0 || stateChanges.Count > 0; attempt++)
        {
            db.Measurements.AddRange(measurements);
            db.StateChanges.AddRange(stateChanges);

            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException dx) when (attempt < MaxAttempts && IsUniqueViolation(dx, out var constraintName))
            {
                logger.LogInformation("Duplicate row(s) hit a race during bulk insert (attempt {Attempt}), re-filtering and retrying", attempt);
                db.ChangeTracker.Clear();

                var channelIds = measurements.Select(m => m.ChannelId).Concat(stateChanges.Select(s => s.ChannelId)).ToArray();
                var timestamps = measurements.Select(m => m.Timestamp).Concat(stateChanges.Select(s => s.Timestamp)).ToArray();

                measurements = constraintName == EfMeasurement.UniqueIndexName
                    ? await ExcludeExisting(measurements, channelIds, timestamps)
                    : measurements;
                stateChanges = constraintName == EfStateChange.UniqueIndexName
                    ? await ExcludeExisting(stateChanges, channelIds, timestamps)
                    : stateChanges;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to bulk insert {MeasurementCount} measurements and {StateChangeCount} state changes", measurements.Count, stateChanges.Count);
                throw;
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException dx, out string constraintName)
    {
        if (dx.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ex && ex.ConstraintName is not null)
        {
            constraintName = ex.ConstraintName;
            return constraintName is EfMeasurement.UniqueIndexName or EfStateChange.UniqueIndexName;
        }

        constraintName = "";
        return false;
    }
}
