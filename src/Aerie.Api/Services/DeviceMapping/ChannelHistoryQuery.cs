using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// DB-facing query for the admin history graph endpoints - loads
/// Measurement/StateChange rows for a set of channels in [from, to] and
/// hands them to ChannelHistoryMath.Bucket for the numeric side, leaving
/// state changes as raw ordered steps (they're already sparse "on change"
/// events, unlike Measurements which warrant downsampling).
/// </summary>
public static class ChannelHistoryQuery
{
    public static async Task<List<ChannelHistoryDto>> GetAsync(
        AerieContext db, IReadOnlyList<EfDeviceChannel> channels, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct)
    {
        var channelIds = channels.Select(c => c.Id).ToList();

        var measurements = await db.Measurements.AsNoTracking()
            .Where(m => channelIds.Contains(m.ChannelId) && m.Timestamp >= from && m.Timestamp <= to)
            .Select(m => new { m.ChannelId, m.Timestamp, m.Value })
            .ToListAsync(ct);

        var states = await db.StateChanges.AsNoTracking()
            .Where(s => channelIds.Contains(s.ChannelId) && s.Timestamp >= from && s.Timestamp <= to)
            .OrderBy(s => s.Timestamp)
            .Select(s => new { s.ChannelId, s.Timestamp, s.State })
            .ToListAsync(ct);

        return channels.Select(c => new ChannelHistoryDto(
            c.Id,
            c.Metric,
            ChannelHistoryMath.Bucket(
                measurements.Where(m => m.ChannelId == c.Id).Select(m => (m.Timestamp, m.Value)), from, to, bucket),
            states.Where(s => s.ChannelId == c.Id).Select(s => new ChannelStatePoint(s.Timestamp, s.State)).ToList()
        )).ToList();
    }
}
