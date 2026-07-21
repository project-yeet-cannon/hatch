using System.Globalization;
using Aerie.Api.Ef;
using HADotNet.Core.Clients;
using HADotNet.Core.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
public class SampleChannels(TimeProvider t, HistoryClient haHistory, AerieContext db) : IAerieJob
{
    public string Name => "SampleChannels";

    public string Group => "Aerie.Api";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task Execute(IJobExecutionContext context)
    {
        var now = t.GetUtcNow();
        var then = now.Subtract(Interval * 2);

        var channels = await db.DeviceChannels
            .AsNoTracking()
            .Include(c => c.Device)
            .Where(c => c.Device!.Enabled)
            .ToListAsync();

        foreach (var group in channels.GroupBy(c => c.HaEntityId))
        {
            var history = await haHistory.GetHistory(group.Key, then, now);
            if (history is null) continue;

            foreach (var channel in group)
            {
                foreach (var state in history)
                {
                    var (numeric, text) = ExtractValue(state, channel);
                    if (numeric is decimal value)
                        await InsertMeasurement(channel.Id, state.LastUpdated, value);
                    else if (text is not null)
                        await InsertStateChange(channel.Id, state.LastUpdated, text);
                }
            }
        }
    }

    /// <summary>
    /// Reads a channel's value out of a historical HA state: the bare state
    /// string when HaAttribute is null (hygrometer entities), or the named
    /// attribute (thermostat entities) otherwise. HA numbers arrive boxed as
    /// long/double; anything that doesn't parse as a decimal (e.g.
    /// hvac_action's "heating"/"idle") is returned as text instead.
    /// </summary>
    private static (decimal? Numeric, string? Text) ExtractValue(StateObject state, EfDeviceChannel channel)
    {
        object? raw = channel.HaAttribute is null
            ? state.State
            : state.Attributes.GetValueOrDefault(channel.HaAttribute);

        return raw switch
        {
            null => (null, null),
            long l => ((decimal)l, null),
            double d => ((decimal)d, null),
            decimal m => (m, null),
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => (parsed, null),
            string s => (null, s),
            _ => (null, raw.ToString())
        };
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
            db.ChangeTracker.Clear();
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
            db.ChangeTracker.Clear();
        }
    }

    private static bool IsUniqueViolation(DbUpdateException dx, string constraintName) =>
        dx.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ex
        && ex.ConstraintName == constraintName;
}
