using Hatch.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Services.Dashboard;

/// <summary>
/// Shared query helpers over Measurement rows scoped to a Zone's enabled
/// devices, used by both ZoneService and WeatherService - the Outside card
/// reads a Zone(Kind = Outside) the same way an interior zone card does.
/// </summary>
internal static class ZoneMeasurements
{
    public static IQueryable<EfMeasurement> ForZone(
        AppDbContext db, Guid zoneId, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to) =>
        db.Measurements.AsNoTracking()
            .Where(m => m.Channel!.Metric == metric
                && m.Channel.Device!.ZoneId == zoneId
                && m.Channel.Device.Enabled
                && m.Timestamp >= from && m.Timestamp <= to);

    /// <summary>
    /// The (channel id -> zone id) map for every enabled channel of the given
    /// metric across a set of zones. Callers that would otherwise loop
    /// <see cref="ForZone"/> per zone use this to resolve every matching
    /// channel for the whole zone set in one query, then filter Measurements
    /// by channel id (<c>channelZones.Keys.ToList()</c>) in a second -
    /// two round trips total regardless of how many zones are on the
    /// dashboard, instead of two per zone.
    /// </summary>
    public static async Task<Dictionary<Guid, Guid>> ChannelZonesAsync(
        AppDbContext db, IReadOnlyList<Guid> zoneIds, DeviceChannelMetric metric, CancellationToken ct)
    {
        // Projected server-side first (rather than passing navigation-based
        // key/element selectors straight to ToDictionaryAsync) so the
        // dictionary is built from plain materialized values instead of
        // dereferencing an unloaded Device navigation on the tracked entity.
        var rows = await db.DeviceChannels.AsNoTracking()
            .Where(c => c.Metric == metric
                && c.Device!.ZoneId != null
                && zoneIds.Contains(c.Device.ZoneId!.Value)
                && c.Device.Enabled)
            .Select(c => new { c.Id, ZoneId = c.Device!.ZoneId!.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id, r => r.ZoneId);
    }

    public static Task<decimal?> LatestAsync(
        AppDbContext db, Guid zoneId, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ForZone(db, zoneId, metric, from, to)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => (decimal?)m.Value)
            .FirstOrDefaultAsync(ct);
}
