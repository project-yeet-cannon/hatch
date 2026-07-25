using Aerie.Api.Models.Dashboard;

namespace Aerie.Api.Services.Dashboard;

/// <summary>
/// NOAA solar position algorithm (https://gml.noaa.gov/grad/solcalc/calcdetails.html),
/// used to compute sun events locally instead of round-tripping through Home
/// Assistant's sun.sun entity (see WeatherService). Single-pass, no
/// iteration - accurate to well under a minute, which is all "hours until
/// sunset" or dashboard theming need. Not valid inside the polar circles, where
/// the sun can stay up or down all day and the hour-angle formula below has no
/// solution.
/// </summary>
public static class SolarCalculator
{
    private const double SunriseSunsetAngleDeg = 90.833; // 90° + atmospheric refraction + solar disc radius
    private const double CivilTwilightAngleDeg = 96.0; // 90° + 6°, civil twilight - swap for nautical (102°) or astronomical (108°) here if the theme's "full dark" window should start/end differently

    private enum SunDirection { Rising, Setting }

    /// <summary>The next sunset (UTC) at or after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextSunset(DateTimeOffset now, double latitudeDeg, double longitudeDeg) =>
        NextEvent(now, latitudeDeg, longitudeDeg, SunriseSunsetAngleDeg, SunDirection.Setting);

    /// <summary>
    /// The day's four sun events - civil dawn, sunrise, sunset, and civil dusk -
    /// for the UTC calendar date <paramref name="now"/> falls on. Callers place
    /// `now` in a light/dark cycle with plain range checks against these four
    /// instants and don't need to special-case midnight: if `now` precedes
    /// today's dawn it's still last night, and if it's past today's dusk it's
    /// already tonight - both correctly read as "night" without ever looking at
    /// yesterday's or tomorrow's events.
    /// </summary>
    public static SunEvents EventsForDay(DateTimeOffset now, double latitudeDeg, double longitudeDeg)
    {
        var date = now.UtcDateTime.Date;
        return new SunEvents(
            Dawn: EventUtc(date, latitudeDeg, longitudeDeg, CivilTwilightAngleDeg, SunDirection.Rising),
            Sunrise: EventUtc(date, latitudeDeg, longitudeDeg, SunriseSunsetAngleDeg, SunDirection.Rising),
            Sunset: EventUtc(date, latitudeDeg, longitudeDeg, SunriseSunsetAngleDeg, SunDirection.Setting),
            Dusk: EventUtc(date, latitudeDeg, longitudeDeg, CivilTwilightAngleDeg, SunDirection.Setting));
    }

    private static DateTimeOffset NextEvent(DateTimeOffset now, double latitudeDeg, double longitudeDeg, double angleDeg, SunDirection direction)
    {
        var today = EventUtc(now.UtcDateTime.Date, latitudeDeg, longitudeDeg, angleDeg, direction);
        return today >= now
            ? today
            : EventUtc(now.UtcDateTime.Date.AddDays(1), latitudeDeg, longitudeDeg, angleDeg, direction);
    }

    /// <summary>The event time (UTC) on the given UTC calendar date, evaluated at UTC noon per the NOAA formula.</summary>
    private static DateTimeOffset EventUtc(DateTime utcDate, double latitudeDeg, double longitudeDeg, double angleDeg, SunDirection direction)
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
        var haCos = Math.Cos(ToRad(angleDeg)) / (Math.Cos(latRad) * Math.Cos(declRad)) - Math.Tan(latRad) * Math.Tan(declRad);
        var haDeg = ToDeg(Math.Acos(Math.Clamp(haCos, -1.0, 1.0)));
        var signedHaDeg = direction == SunDirection.Setting ? haDeg : -haDeg;

        var solarNoonFraction = (720.0 - 4.0 * longitudeDeg - eqOfTimeMinutes) / 1440.0;
        var eventFraction = solarNoonFraction + signedHaDeg * 4.0 / 1440.0;

        return new DateTimeOffset(utcDate, TimeSpan.Zero).AddDays(eventFraction);
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
