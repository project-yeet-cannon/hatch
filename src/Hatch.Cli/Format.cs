using System.Globalization;

namespace Hatch.Cli;

/// <summary>Numbers, as somebody reads them off a terminal at midnight.</summary>
public static class Format
{
    /// <summary>Seconds, with the hours only where there are some.</summary>
    public static string Duration(long seconds) =>
        seconds >= 3600
            ? $"{seconds / 3600}h{seconds % 3600 / 60:00}m{seconds % 60:00}s"
            : $"{seconds / 60}m{seconds % 60:00}s";

    /// <summary>Dollars, the way a bill is written.</summary>
    public static string Money(decimal usd) => usd.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Dollars at enough places that a cheap session does not read as free. Two
    /// would print a third of a cent as $0, which is a different claim.
    /// </summary>
    public static string Spent(decimal usd) =>
        usd >= 0.01m
            ? "$" + Math.Round(usd, 2).ToString("0.##", CultureInfo.InvariantCulture)
            : "$" + Math.Round(usd, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// A timestamp as the server wrote it: ISO 8601, and no trailing zeros on
    /// the fraction.
    /// </summary>
    /// <remarks>
    /// <c>"O"</c> pads the fraction to seven digits, so a comment the API served
    /// as <c>…:07.38376+00:00</c> would print as <c>…:07.3837600+00:00</c> - the
    /// same instant, spelled differently from everywhere else that quotes it.
    /// <c>F</c> is the digit-if-nonzero form, which round-trips what was sent.
    /// </remarks>
    public static string Stamp(DateTimeOffset when) =>
        when.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    /// <summary>A token count at the resolution a headline wants.</summary>
    public static string Compact(long count) => count switch
    {
        >= 1_000_000 => (Math.Round(count / 100_000m) / 10).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => Math.Round(count / 1_000m).ToString("0", CultureInfo.InvariantCulture) + "k",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };
}
