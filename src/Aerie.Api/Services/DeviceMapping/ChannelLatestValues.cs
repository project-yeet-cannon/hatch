using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>A channel's most recent sample - numeric (Measurement) or text (StateChange), whichever is newer. All-null when the channel has no samples yet.</summary>
public readonly record struct ChannelLatestValue(decimal? Value, string? State, DateTimeOffset? Timestamp);

/// <summary>
/// Batched "latest row per channel" lookup for the admin Devices UI. Uses a
/// NOT EXISTS anti-join rather than GroupBy+OrderByDescending+First: that
/// GroupBy shape's SQL translation is uncertain on Npgsql and would still
/// pass against the InMemory test provider even if it mistranslated for
/// real Postgres, so the anti-join (a plain correlated subquery every
/// provider translates identically) is the only form that makes the unit
/// tests trustworthy. Index-friendly given EfMeasurement/EfStateChange's
/// existing unique (ChannelId, Timestamp) index.
/// </summary>
public static class ChannelLatestValues
{
    public static async Task<Dictionary<Guid, ChannelLatestValue>> GetLatestAsync(
        AerieContext db, IReadOnlyCollection<Guid> channelIds, CancellationToken ct)
    {
        if (channelIds.Count == 0) return [];

        var measurements = await db.Measurements.AsNoTracking()
            .Where(m => channelIds.Contains(m.ChannelId))
            .Where(m => !db.Measurements.Any(m2 => m2.ChannelId == m.ChannelId && m2.Timestamp > m.Timestamp))
            .Select(m => new { m.ChannelId, m.Timestamp, m.Value })
            .ToListAsync(ct);

        var states = await db.StateChanges.AsNoTracking()
            .Where(s => channelIds.Contains(s.ChannelId))
            .Where(s => !db.StateChanges.Any(s2 => s2.ChannelId == s.ChannelId && s2.Timestamp > s.Timestamp))
            .Select(s => new { s.ChannelId, s.Timestamp, s.State })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, ChannelLatestValue>();
        foreach (var id in channelIds)
        {
            var m = measurements.FirstOrDefault(x => x.ChannelId == id);
            var s = states.FirstOrDefault(x => x.ChannelId == id);
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
