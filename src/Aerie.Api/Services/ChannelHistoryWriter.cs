using Aerie.Api.Ef;
using HADotNet.Core.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aerie.Api.Services;

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

public class ChannelHistoryWriter(AerieContext db, ILogger<ChannelHistoryWriter> logger) : IChannelHistoryWriter
{
    public async Task<ChannelWriteCounts> WriteAsync(IReadOnlyCollection<EfDeviceChannel> channels, HistoryList history)
    {
        var measurementCount = 0;
        var stateChangeCount = 0;

        foreach (var channel in channels)
        {
            foreach (var state in history)
            {
                var (numeric, text) = ChannelValueExtractor.Extract(state, channel);
                if (numeric is decimal value)
                {
                    await InsertMeasurement(channel.Id, state.LastUpdated, value);
                    measurementCount++;
                }
                else if (text is not null)
                {
                    await InsertStateChange(channel.Id, state.LastUpdated, text);
                    stateChangeCount++;
                }
            }
        }

        return new ChannelWriteCounts(measurementCount, stateChangeCount);
    }

    private async Task InsertMeasurement(Guid channelId, DateTimeOffset timestamp, decimal value)
    {
        try
        {
            db.Measurements.Add(new EfMeasurement { ChannelId = channelId, Timestamp = timestamp, Value = value });
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException dx) when (IsUniqueViolation(dx, EfMeasurement.UniqueIndexName))
        {
            logger.LogInformation("Duplicate measurement ignored for channel {ChannelId} at {Timestamp}", channelId, timestamp);
            db.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to insert measurement for channel {ChannelId} at {Timestamp} with value {Value}", channelId, timestamp, value);
            throw;
        }
    }

    private async Task InsertStateChange(Guid channelId, DateTimeOffset timestamp, string state)
    {
        try
        {
            db.StateChanges.Add(new EfStateChange { ChannelId = channelId, Timestamp = timestamp, State = state });
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException dx) when (IsUniqueViolation(dx, EfStateChange.UniqueIndexName))
        {
            logger.LogInformation("Duplicate state change ignored for channel {ChannelId} at {Timestamp}", channelId, timestamp);
            db.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to insert state change for channel {ChannelId} at {Timestamp} with state {State}", channelId, timestamp, state);
            throw;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException dx, string constraintName) =>
        dx.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ex
        && ex.ConstraintName == constraintName;
}
