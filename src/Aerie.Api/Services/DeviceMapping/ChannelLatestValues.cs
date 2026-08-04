using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>A channel's most recent sample - numeric (Measurement) or text (StateChange), whichever is newer. All-null when the channel has no samples yet.</summary>
public readonly record struct ChannelLatestValue(decimal? Value, string? State, DateTimeOffset? Timestamp);

/// <summary>
/// Batched "latest row per channel" lookup for the admin Devices UI. Issues
/// one indexed "ORDER BY Timestamp DESC LIMIT 1" lookup per channel per table
/// rather than a single set-based query over all requested channels: a
/// NOT-EXISTS anti-join or GroupBy+OrderByDescending+First still has to scan
/// every Measurement/StateChange row belonging to the requested channels to
/// find each one's latest, so cost grows with total row count rather than
/// channel count - on Devices' unscoped "every channel in the system" call,
/// that scan grows unbounded as history accumulates (there's no retention
/// job) and eventually blows Npgsql's command timeout. A per-channel lookup
/// against EfMeasurement/EfStateChange's existing unique (ChannelId,
/// Timestamp) index is a direct index descent to the last row - O(channels),
/// not O(rows) - and uses only Where+OrderByDescending+FirstOrDefault, which
/// every provider (including the InMemory test provider) translates the same
/// way, unlike GroupBy's uncertain Npgsql translation.
/// </summary>
public static class ChannelLatestValues
{
    public static async Task<Dictionary<Guid, ChannelLatestValue>> GetLatestAsync(
        AerieContext db, IReadOnlyCollection<Guid> channelIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, ChannelLatestValue>();

        foreach (var id in channelIds)
        {
            var m = await db.Measurements.AsNoTracking()
                .Where(x => x.ChannelId == id)
                .OrderByDescending(x => x.Timestamp)
                .Select(x => new { x.Timestamp, x.Value })
                .FirstOrDefaultAsync(ct);

            var s = await db.StateChanges.AsNoTracking()
                .Where(x => x.ChannelId == id)
                .OrderByDescending(x => x.Timestamp)
                .Select(x => new { x.Timestamp, x.State })
                .FirstOrDefaultAsync(ct);

            result[id] = (m, s) switch
            {
                (not null, not null) when s.Timestamp > m.Timestamp => new ChannelLatestValue(null, s.State, s.Timestamp),
                (not null, _) => new ChannelLatestValue(m.Value, null, m.Timestamp),
                (null, not null) => new ChannelLatestValue(null, s.State, s.Timestamp),
                _ => new ChannelLatestValue(null, null, null),
            };
        }
        return result;
    }
}
