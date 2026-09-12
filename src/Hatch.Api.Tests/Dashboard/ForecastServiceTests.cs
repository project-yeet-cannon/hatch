using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.Dashboard;

namespace Hatch.Api.Tests.Dashboard;

public class ForecastServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 15, 0, 0, TimeSpan.Zero);
    private readonly ForecastService _svc = new();

    private static List<TempPoint> Rising() =>
        Enumerable.Range(0, 6)
            .Select(i => new TempPoint(Now.AddMinutes(-30 * (6 - i)), 68m + i))
            .ToList();

    [Fact]
    public void Project_StartsAtNowWithCurrentTemp()
    {
        var forecast = _svc.Project(Rising(), 73m, Now, TimeSpan.FromHours(3), TimeSpan.FromMinutes(30));

        Assert.NotEmpty(forecast);
        Assert.Equal(Now, forecast[0].Time);
        Assert.Equal(73m, forecast[0].TempF);
    }

    [Fact]
    public void Project_ContinuesRisingTrendButDampens()
    {
        var forecast = _svc.Project(Rising(), 73m, Now, TimeSpan.FromHours(3), TimeSpan.FromMinutes(30));

        // Trend is up, so the next point should be warmer than now...
        Assert.True(forecast[1].TempF > forecast[0].TempF);
        // ...but damping means each increment is smaller than the previous one.
        var firstStep = forecast[1].TempF - forecast[0].TempF;
        var lastStep = forecast[^1].TempF - forecast[^2].TempF;
        Assert.True(lastStep < firstStep);
    }

    [Fact]
    public void Project_HasCorrectPointCountForHorizon()
    {
        var forecast = _svc.Project(Rising(), 73m, Now, TimeSpan.FromHours(2), TimeSpan.FromMinutes(30));
        // now + 4 half-hour steps
        Assert.Equal(5, forecast.Count);
        Assert.Equal(Now.AddHours(2), forecast[^1].Time);
    }

    [Fact]
    public void Project_FlatWhenHistoryTooShort()
    {
        var forecast = _svc.Project([], 70m, Now, TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));
        Assert.All(forecast, p => Assert.Equal(70m, p.TempF));
    }
}
