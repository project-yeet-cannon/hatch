using Hatch.Api.Services.Dashboard;

namespace Hatch.Api.Tests.Dashboard;

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

    [Fact]
    public void EventsForDay_OrdersDawnSunriseSunsetDusk()
    {
        var events = SolarCalculator.EventsForDay(new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero), NycLat, NycLon);

        Assert.True(events.Dawn < events.Sunrise, "dawn should precede sunrise");
        Assert.True(events.Sunrise < events.Sunset, "sunrise should precede sunset");
        Assert.True(events.Sunset < events.Dusk, "sunset should precede dusk");
    }

    [Fact]
    public void EventsForDay_Sunset_MatchesNextSunset()
    {
        var now = new DateTimeOffset(2026, 12, 21, 12, 0, 0, TimeSpan.Zero);

        var events = SolarCalculator.EventsForDay(now, NycLat, NycLon);
        var sunset = SolarCalculator.NextSunset(now, NycLat, NycLon);

        Assert.Equal(sunset, events.Sunset);
    }

    [Fact]
    public void EventsForDay_CivilTwilightDuration_IsRoughlyTwentyToThirtyMinutesAtMidLatitude()
    {
        var events = SolarCalculator.EventsForDay(new DateTimeOffset(2026, 3, 21, 12, 0, 0, TimeSpan.Zero), NycLat, NycLon);

        Assert.InRange((events.Sunrise - events.Dawn).TotalMinutes, 15, 35);
        Assert.InRange((events.Dusk - events.Sunset).TotalMinutes, 15, 35);
    }

    [Fact]
    public void EventsForDay_DependsOnlyOnUtcCalendarDate_NotTimeOfDay()
    {
        // Any two instants on the same UTC calendar date - including just after
        // midnight - must resolve to the same quartet, since that's what lets
        // callers place "now" in a light/dark cycle without special-casing
        // midnight (see the doc comment on EventsForDay).
        var earlyMorning = SolarCalculator.EventsForDay(new DateTimeOffset(2026, 6, 21, 0, 0, 1, TimeSpan.Zero), NycLat, NycLon);
        var lateNight = SolarCalculator.EventsForDay(new DateTimeOffset(2026, 6, 21, 23, 59, 59, TimeSpan.Zero), NycLat, NycLon);

        Assert.Equal(earlyMorning, lateNight);
    }
}
