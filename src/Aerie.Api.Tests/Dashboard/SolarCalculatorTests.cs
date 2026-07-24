using Aerie.Api.Services.Dashboard;

namespace Aerie.Api.Tests.Dashboard;

public class SolarCalculatorTests
{
    // New York City. Published sunset times are ~20:31 EDT on the June
    // solstice and ~16:32 EST on the December solstice - both UTC-adjusted
    // below, with a few minutes of slack for the almanac source's rounding.
    private const double NycLat = 40.7128;
    private const double NycLon = -74.0060;

    [Fact]
    public void NextSunset_JuneSolstice_MatchesPublishedNycSunset()
    {
        var noon = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 6, 22, 0, 31, 0, TimeSpan.Zero); // 20:31 EDT

        var sunset = SolarCalculator.NextSunset(noon, NycLat, NycLon);

        Assert.True(Math.Abs((sunset - expected).TotalMinutes) < 3, $"expected ~{expected:O}, got {sunset:O}");
    }

    [Fact]
    public void NextSunset_DecemberSolstice_MatchesPublishedNycSunset()
    {
        var noon = new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 12, 21, 21, 32, 0, TimeSpan.Zero); // 16:32 EST

        var sunset = SolarCalculator.NextSunset(noon, NycLat, NycLon);

        Assert.True(Math.Abs((sunset - expected).TotalMinutes) < 3, $"expected ~{expected:O}, got {sunset:O}");
    }

    [Fact]
    public void NextSunset_AfterTodaysSunset_RollsOverToTomorrow()
    {
        var today = SolarCalculator.NextSunset(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero), NycLat, NycLon);
        var afterSunset = today.AddHours(1);

        var next = SolarCalculator.NextSunset(afterSunset, NycLat, NycLon);

        Assert.True(next > afterSunset);
        Assert.InRange((next - today).TotalHours, 23, 25); // rolled forward to the following evening, not today's again
    }

    [Fact]
    public void NextSunset_SouthernHemisphere_IsEarlierInJuneThanDecember()
    {
        // Melbourne, Australia - seasons inverted relative to NYC.
        const double lat = -37.8136;
        const double lon = 144.9631;

        var juneSunset = SolarCalculator.NextSunset(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero), lat, lon);
        var decemberSunset = SolarCalculator.NextSunset(new DateTimeOffset(2026, 12, 21, 0, 0, 0, TimeSpan.Zero), lat, lon);

        var juneLocalHour = juneSunset.AddHours(11).Hour; // AEST, UTC+11 in December (daylight) / +10 in June - close enough to bucket by hour
        var decemberLocalHour = decemberSunset.AddHours(11).Hour;

        Assert.True(juneLocalHour < decemberLocalHour, $"expected June ({juneLocalHour}) earlier than December ({decemberLocalHour})");
    }
}
