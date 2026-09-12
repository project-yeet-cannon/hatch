using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.Dashboard;

namespace Hatch.Api.Tests.Dashboard;

public class ZoneMathTests
{
    private static readonly DateTimeOffset From = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bucket_AveragesReadingsWithinEachBucket()
    {
        var bucket = TimeSpan.FromMinutes(30);
        var readings = new (DateTimeOffset, decimal?)[]
        {
            (From.AddMinutes(0), 70m),
            (From.AddMinutes(10), 72m),   // same 30-min bucket as above -> avg 71
            (From.AddMinutes(35), 74m),   // next bucket
        };

        var result = ZoneMath.Bucket(readings, From, From.AddHours(1), bucket);

        Assert.Equal(2, result.Count);
        Assert.Equal(71m, result[0].TempF);
        Assert.Equal(From, result[0].Time);
        Assert.Equal(74m, result[1].TempF);
    }

    [Fact]
    public void Bucket_DropsNullAndOutOfWindowReadings()
    {
        var readings = new (DateTimeOffset, decimal?)[]
        {
            (From.AddMinutes(-10), 60m), // before window
            (From.AddMinutes(5), null),  // null temp
            (From.AddMinutes(15), 70m),  // kept
            (From.AddHours(3), 80m),     // after window
        };

        var result = ZoneMath.Bucket(readings, From, From.AddHours(1), TimeSpan.FromMinutes(30));

        Assert.Single(result);
        Assert.Equal(70m, result[0].TempF);
    }

    [Fact]
    public void Bucket_ResultIsOldestFirst()
    {
        var readings = new (DateTimeOffset, decimal?)[]
        {
            (From.AddMinutes(90), 73m),
            (From.AddMinutes(0), 70m),
            (From.AddMinutes(45), 71m),
        };

        var result = ZoneMath.Bucket(readings, From, From.AddHours(2), TimeSpan.FromMinutes(30));

        Assert.Equal(new[] { 70m, 71m, 73m }, result.Select(p => p.TempF));
    }

    [Fact]
    public void Extremes_ReturnsRoundedLowAndHighWithTimes()
    {
        var points = new List<TempPoint>
        {
            new(From, 68.4m),
            new(From.AddMinutes(30), 74.6m),
            new(From.AddMinutes(60), 70m),
        };

        var extremes = ZoneMath.Extremes(points);

        Assert.NotNull(extremes);
        Assert.Equal(68m, extremes!.Value.Low.TempF);
        Assert.Equal(From, extremes.Value.Low.Time);
        Assert.Equal(75m, extremes.Value.High.TempF);
        Assert.Equal(From.AddMinutes(30), extremes.Value.High.Time);
    }

    [Fact]
    public void Extremes_NullWhenEmpty() => Assert.Null(ZoneMath.Extremes([]));
}
