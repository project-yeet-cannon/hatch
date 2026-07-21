using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Dashboard;

/// <summary>
/// Shared query helpers over Measurement rows scoped to a Zone's enabled
/// devices, used by both ZoneService and WeatherService - the Outside card
/// reads a Zone(Kind = Outside) the same way an interior zone card does.
/// </summary>
internal static class ZoneMeasurements
{
    public static IQueryable<EfMeasurement> ForZone(
        AerieContext db, Guid zoneId, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to) =>
        db.Measurements.AsNoTracking()
            .Where(m => m.Channel!.Metric == metric
                && m.Channel.Device!.ZoneId == zoneId
                && m.Channel.Device.Enabled
                && m.Timestamp >= from && m.Timestamp <= to);

    public static Task<decimal?> LatestAsync(
        AerieContext db, Guid zoneId, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ForZone(db, zoneId, metric, from, to)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => (decimal?)m.Value)
            .FirstOrDefaultAsync(ct);
}
