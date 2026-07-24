namespace Aerie.Api.Services.Dashboard;

/// <summary>
/// NOAA solar position algorithm (https://gml.noaa.gov/grad/solcalc/calcdetails.html),
/// used to compute sunset locally instead of round-tripping through Home
/// Assistant's sun.sun entity (see WeatherService). Single-pass, no
/// iteration - accurate to well under a minute, which is all "hours until
/// sunset" needs. Not valid inside the polar circles, where the sun can stay
/// up or down all day and the hour-angle formula below has no solution.
/// </summary>
public static class SolarCalculator
{
    private const double SunsetAngleDeg = 90.833; // 90° + atmospheric refraction + solar disc radius

    /// <summary>The next sunset (UTC) at or after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextSunset(DateTimeOffset now, double latitudeDeg, double longitudeDeg)
    {
        var today = SunsetUtc(now.UtcDateTime.Date, latitudeDeg, longitudeDeg);
        return today >= now ? today : SunsetUtc(now.UtcDateTime.Date.AddDays(1), latitudeDeg, longitudeDeg);
    }

    /// <summary>Sunset time (UTC) on the given UTC calendar date, evaluated at UTC noon per the NOAA formula.</summary>
    private static DateTimeOffset SunsetUtc(DateTime utcDate, double latitudeDeg, double longitudeDeg)
    {
        var t = JulianCentury(utcDate.AddHours(12));

        var l0 = Mod360(280.46646 + t * (36000.76983 + t * 0.0003032));
        var m = 357.52911 + t * (35999.05029 - 0.0001537 * t);
        var e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);

        var mRad = ToRad(m);
        var sunEqOfCtr = Math.Sin(mRad) * (1.914602 - t * (0.004817 + 0.000014 * t))
            + Math.Sin(2 * mRad) * (0.019993 - 0.000101 * t)
            + Math.Sin(3 * mRad) * 0.000289;

        var sunTrueLong = l0 + sunEqOfCtr;
        var sunAppLong = sunTrueLong - 0.00569 - 0.00478 * Math.Sin(ToRad(125.04 - 1934.136 * t));

        var meanObliq = 23.0 + (26.0 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60.0) / 60.0;
        var obliqCorr = meanObliq + 0.00256 * Math.Cos(ToRad(125.04 - 1934.136 * t));

        var decl = ToDeg(Math.Asin(Math.Sin(ToRad(obliqCorr)) * Math.Sin(ToRad(sunAppLong))));

        var y = Math.Pow(Math.Tan(ToRad(obliqCorr / 2.0)), 2);
        var eqOfTimeMinutes = 4.0 * ToDeg(
            y * Math.Sin(2 * ToRad(l0))
            - 2 * e * Math.Sin(mRad)
            + 4 * e * y * Math.Sin(mRad) * Math.Cos(2 * ToRad(l0))
            - 0.5 * y * y * Math.Sin(4 * ToRad(l0))
            - 1.25 * e * e * Math.Sin(2 * mRad));

        var latRad = ToRad(latitudeDeg);
        var declRad = ToRad(decl);
        var haCos = Math.Cos(ToRad(SunsetAngleDeg)) / (Math.Cos(latRad) * Math.Cos(declRad)) - Math.Tan(latRad) * Math.Tan(declRad);
        var haSunsetDeg = ToDeg(Math.Acos(Math.Clamp(haCos, -1.0, 1.0)));

        var solarNoonFraction = (720.0 - 4.0 * longitudeDeg - eqOfTimeMinutes) / 1440.0;
        var sunsetFraction = solarNoonFraction + haSunsetDeg * 4.0 / 1440.0;

        return new DateTimeOffset(utcDate, TimeSpan.Zero).AddDays(sunsetFraction);
    }

    private static double JulianCentury(DateTime utc)
    {
        double year = utc.Year, month = utc.Month;
        var day = utc.Day + utc.TimeOfDay.TotalHours / 24.0;
        if (month <= 2) { year -= 1; month += 12; }
        var a = Math.Floor(year / 100.0);
        var b = 2 - a + Math.Floor(a / 4.0);
        var julianDay = Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
        return (julianDay - 2451545.0) / 36525.0;
    }

    private static double Mod360(double deg) => ((deg % 360) + 360) % 360;
    private static double ToRad(double deg) => deg * Math.PI / 180.0;
    private static double ToDeg(double rad) => rad * 180.0 / Math.PI;
}
