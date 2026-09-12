using Hatch.Api.Models.DeviceMapping;

namespace Hatch.Api.Services.DeviceMapping;

/// <summary>
/// Pure time-series bucketing for the admin history graph - no DB, no clock.
/// Same shape as Services/Dashboard/ZoneMath.Bucket, but Devices-scoped
/// (non-nullable decimal, matching EfMeasurement.Value) rather than tied to
/// the Dashboard-specific TempPoint model.
/// </summary>
public static class ChannelHistoryMath
{
    /// <summary>
    /// Downsamples raw readings into fixed-width buckets, averaging the
    /// value in each. Out-of-window rows are dropped; empty buckets simply
    /// don't appear. Result is oldest-first.
    /// </summary>
    public static IReadOnlyList<ChannelHistoryPoint> Bucket(
        IEnumerable<(DateTimeOffset Ts, decimal Value)> readings,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bucket));

        return readings
            .Where(r => r.Ts >= from && r.Ts <= to)
            .GroupBy(r => BucketStart(r.Ts, from, bucket))
            .Select(g => new ChannelHistoryPoint(g.Key, Math.Round(g.Average(x => x.Value), 2)))
            .OrderBy(p => p.Time)
            .ToList();
    }

    private static DateTimeOffset BucketStart(DateTimeOffset ts, DateTimeOffset from, TimeSpan bucket)
    {
        var n = (ts - from).Ticks / bucket.Ticks;
        return from + TimeSpan.FromTicks(n * bucket.Ticks);
    }
}
