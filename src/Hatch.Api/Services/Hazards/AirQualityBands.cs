namespace Hatch.Api.Services.Hazards;

/// <summary>
/// The six bands the US AQI scale is defined in. Ordered by how bad the air
/// is, so a comparison between two bands is meaningful and a severity mapping
/// can be written as a switch rather than a chain of numeric bounds.
/// </summary>
public enum AirQualityBand
{
    Good,
    Moderate,
    UnhealthyForSensitiveGroups,
    Unhealthy,
    VeryUnhealthy,
    Hazardous,
}

/// <summary>
/// US AQI to the band it falls in, and the band to the words a person reads.
///
/// Pure and kept apart from any provider on purpose: the bands are the EPA's
/// definition of the index, not Open-Meteo's presentation of it, so a second
/// provider reporting US AQI shares this rather than restating it. The display
/// string lives here too, so "Unhealthy for Sensitive Groups" is spelled once
/// instead of once per surface that shows it.
/// </summary>
public static class AirQualityBands
{
    /// <summary>
    /// The band an index value falls in. The boundaries are inclusive lower
    /// bounds (51 is Moderate, 50 is Good); anything at or above 301 is
    /// Hazardous, which is where the official scale stops naming ranges.
    /// </summary>
    public static AirQualityBand Of(int usAqi) => usAqi switch
    {
        <= 50 => AirQualityBand.Good,
        <= 100 => AirQualityBand.Moderate,
        <= 150 => AirQualityBand.UnhealthyForSensitiveGroups,
        <= 200 => AirQualityBand.Unhealthy,
        <= 300 => AirQualityBand.VeryUnhealthy,
        _ => AirQualityBand.Hazardous,
    };

    /// <summary>The band's name as the EPA writes it, which is what the kiosk shows as an alert's title.</summary>
    public static string NameOf(AirQualityBand band) => band switch
    {
        AirQualityBand.Good => "Good",
        AirQualityBand.Moderate => "Moderate",
        AirQualityBand.UnhealthyForSensitiveGroups => "Unhealthy for Sensitive Groups",
        AirQualityBand.Unhealthy => "Unhealthy",
        AirQualityBand.VeryUnhealthy => "Very Unhealthy",
        _ => "Hazardous",
    };

    /// <summary>The name of the band an index value falls in - the two calls above, which is how every caller uses them.</summary>
    public static string NameOf(int usAqi) => NameOf(Of(usAqi));
}
