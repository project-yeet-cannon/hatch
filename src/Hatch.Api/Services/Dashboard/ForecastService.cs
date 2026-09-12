using Hatch.Api.Models.Dashboard;

namespace Hatch.Api.Services.Dashboard;

public interface IForecastService
{
    /// <summary>
    /// Projects temperature forward from "now". The returned series starts at
    /// <paramref name="now"/> with <paramref name="currentTempF"/> so it joins
    /// the history line, then extends for <paramref name="horizon"/> at
    /// <paramref name="step"/> intervals.
    /// </summary>
    IReadOnlyList<TempPoint> Project(
        IReadOnlyList<TempPoint> history,
        decimal currentTempF,
        DateTimeOffset now,
        TimeSpan horizon,
        TimeSpan step);
}

/// <summary>
/// Deliberately simple: extrapolates the recent slope, damped toward flat so a
/// steep recent trend doesn't run away over a multi-hour horizon. This is a
/// placeholder for a real thermal/weather-coupled model - it's honest about
/// being a projection (it lives in the separate `forecast` array) and is pure
/// so it can be unit-tested.
/// </summary>
public class ForecastService : IForecastService
{
    private const int RegressionWindow = 6; // last ~3h at 30-min buckets
    private const decimal Damping = 0.82m;
    private const decimal MaxSlopePerStep = 3m; // clamp runaway extrapolation

    public IReadOnlyList<TempPoint> Project(
        IReadOnlyList<TempPoint> history,
        decimal currentTempF,
        DateTimeOffset now,
        TimeSpan horizon,
        TimeSpan step)
    {
        var points = new List<TempPoint> { new(now, Math.Round(currentTempF, 1)) };
        if (horizon <= TimeSpan.Zero || step <= TimeSpan.Zero) return points;

        var slope = Math.Clamp(SlopePerStep(history), -MaxSlopePerStep, MaxSlopePerStep);
        var steps = (int)(horizon.Ticks / step.Ticks);
        var temp = currentTempF;

        for (var i = 1; i <= steps; i++)
        {
            temp += slope;
            slope *= Damping;
            points.Add(new TempPoint(now + TimeSpan.FromTicks(step.Ticks * i), Math.Round(temp, 1)));
        }
        return points;
    }

    /// <summary>
    /// Least-squares slope (°F per bucket) over the last RegressionWindow points.
    /// Buckets are one <c>step</c> apart, so slope-per-index == slope-per-step.
    /// </summary>
    private static decimal SlopePerStep(IReadOnlyList<TempPoint> history)
    {
        if (history.Count < 2) return 0m;

        var take = Math.Min(RegressionWindow, history.Count);
        var pts = history.Skip(history.Count - take).ToList();

        decimal n = take, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < take; i++)
        {
            decimal x = i;
            var y = pts[i].TempF;
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }

        var denom = n * sxx - sx * sx;
        return denom == 0 ? 0m : (n * sxy - sx * sy) / denom;
    }
}
