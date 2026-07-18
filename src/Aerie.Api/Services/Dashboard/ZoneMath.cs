using Aerie.Api.Models.Dashboard;

namespace Aerie.Api.Services.Dashboard;

/// <summary>
/// Pure time-series helpers - no DB, no clock, no HA. Kept separate so they can
/// be unit-tested directly (see Aerie.Api.Tests).
/// </summary>
public static class ZoneMath
{
    /// <summary>
    /// Downsamples raw readings into fixed-width buckets, averaging the
    /// temperature in each. Null temperatures and out-of-window rows are
    /// dropped; empty buckets simply don't appear. Result is oldest-first.
    /// </summary>
    public static IReadOnlyList<TempPoint> Bucket(
        IEnumerable<(DateTimeOffset Ts, decimal? Temp)> readings,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan bucket)
    {
        if (bucket <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bucket));

        return readings
            .Where(r => r.Temp.HasValue && r.Ts >= from && r.Ts <= to)
            .GroupBy(r => BucketStart(r.Ts, from, bucket))
            .Select(g => new TempPoint(g.Key, Math.Round(g.Average(x => x.Temp!.Value), 1)))
            .OrderBy(p => p.Time)
            .ToList();
    }

    /// <summary>Daily low/high over a set of points, rounded to whole degrees. Null if empty.</summary>
    public static (DailyExtreme Low, DailyExtreme High)? Extremes(IEnumerable<TempPoint> points)
    {
        DailyExtreme? low = null;
        DailyExtreme? high = null;
        foreach (var p in points)
        {
            if (low is null || p.TempF < low.TempF) low = new DailyExtreme(Math.Round(p.TempF), p.Time);
            if (high is null || p.TempF > high.TempF) high = new DailyExtreme(Math.Round(p.TempF), p.Time);
        }
        return low is null || high is null ? null : (low, high);
    }

    private static DateTimeOffset BucketStart(DateTimeOffset ts, DateTimeOffset from, TimeSpan bucket)
    {
        var n = (ts - from).Ticks / bucket.Ticks;
        return from + TimeSpan.FromTicks(n * bucket.Ticks);
    }
}
