using Hatch.Api.Services.Hazards;

namespace Hatch.Api.Tests.Hazards;

/// <summary>
/// The boundaries, which are the only part of a band table that can be wrong
/// in a way nobody notices: an off-by-one here reads as "the air is Moderate"
/// on a day it is not.
/// </summary>
public class AirQualityBandsTests
{
    [Theory]
    [InlineData(0, AirQualityBand.Good)]
    [InlineData(50, AirQualityBand.Good)]
    [InlineData(51, AirQualityBand.Moderate)]
    [InlineData(100, AirQualityBand.Moderate)]
    // 101 is the default alert threshold, so this boundary is also where the
    // kiosk starts saying anything at all.
    [InlineData(101, AirQualityBand.UnhealthyForSensitiveGroups)]
    [InlineData(150, AirQualityBand.UnhealthyForSensitiveGroups)]
    [InlineData(151, AirQualityBand.Unhealthy)]
    [InlineData(200, AirQualityBand.Unhealthy)]
    [InlineData(201, AirQualityBand.VeryUnhealthy)]
    [InlineData(300, AirQualityBand.VeryUnhealthy)]
    [InlineData(301, AirQualityBand.Hazardous)]
    [InlineData(900, AirQualityBand.Hazardous)]
    public void MapsAnIndexOntoItsBand(int usAqi, AirQualityBand expected) =>
        Assert.Equal(expected, AirQualityBands.Of(usAqi));

    [Fact]
    public void NamesTheBandTheWayTheScaleDoes() =>
        Assert.Equal("Unhealthy for Sensitive Groups", AirQualityBands.NameOf(120));

    [Fact]
    public void BandsAreOrderedByHowBadTheAirIs() =>
        // HazardService maps bands to severities with a switch that assumes
        // this order, and the peak comparison relies on it too.
        Assert.True(AirQualityBands.Of(160) > AirQualityBands.Of(90));
}
