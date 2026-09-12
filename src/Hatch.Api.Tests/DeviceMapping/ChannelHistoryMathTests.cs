using Hatch.Api.Services.DeviceMapping;

namespace Hatch.Api.Tests.DeviceMapping;

public class ChannelHistoryMathTests
{
    private static readonly DateTimeOffset From = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bucket_ReturnsEmpty_ForEmptyInput() =>
        Assert.Empty(ChannelHistoryMath.Bucket([], From, From.AddHours(1), TimeSpan.FromMinutes(30)));

    [Fact]
    public void Bucket_AveragesReadingsWithinEachBucket()
    {
        var readings = new (DateTimeOffset, decimal)[]
        {
            (From.AddMinutes(0), 70m),
            (From.AddMinutes(10), 72m), // same 30-min bucket -> avg 71
            (From.AddMinutes(35), 74m), // next bucket
        };

        var result = ChannelHistoryMath.Bucket(readings, From, From.AddHours(1), TimeSpan.FromMinutes(30));

        Assert.Equal(2, result.Count);
        Assert.Equal(71m, result[0].Value);
        Assert.Equal(From, result[0].Time);
        Assert.Equal(74m, result[1].Value);
    }

    [Fact]
    public void Bucket_DropsOutOfWindowReadings()
    {
        var readings = new (DateTimeOffset, decimal)[]
        {
            (From.AddMinutes(-10), 60m), // before window
            (From.AddMinutes(15), 70m),  // kept
            (From.AddHours(3), 80m),     // after window
        };

        var result = ChannelHistoryMath.Bucket(readings, From, From.AddHours(1), TimeSpan.FromMinutes(30));

        Assert.Single(result);
        Assert.Equal(70m, result[0].Value);
    }

    [Fact]
    public void Bucket_ResultIsOldestFirst()
    {
        var readings = new (DateTimeOffset, decimal)[]
        {
            (From.AddMinutes(90), 73m),
            (From.AddMinutes(0), 70m),
            (From.AddMinutes(45), 71m),
        };

        var result = ChannelHistoryMath.Bucket(readings, From, From.AddHours(2), TimeSpan.FromMinutes(30));

        Assert.Equal([70m, 71m, 73m], result.Select(p => p.Value));
    }

    [Fact]
    public void Bucket_RoundsAverageToTwoDecimals()
    {
        var readings = new (DateTimeOffset, decimal)[]
        {
            (From, 1m),
            (From, 2m),
            (From, 2m),
        };

        var result = ChannelHistoryMath.Bucket(readings, From, From.AddHours(1), TimeSpan.FromMinutes(30));

        Assert.Equal(1.67m, result[0].Value);
    }

    [Fact]
    public void Bucket_Throws_ForNonPositiveBucketWidth() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChannelHistoryMath.Bucket([], From, From.AddHours(1), TimeSpan.Zero));
}
