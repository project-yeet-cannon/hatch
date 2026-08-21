namespace Aerie.Api.Services.Calendar;

/// <summary>
/// The agenda window, resolved the one way everything that touches it has to
/// resolve it: the same clamp on the operator-typed CalendarAgendaDays and the
/// same timezone fallback. The sync job decides which days get cached and the
/// dashboard read decides which days get rendered, so a disagreement between
/// the two shows up on the wall as a day that is permanently empty.
/// </summary>
public static class CalendarWindow
{
    /// <summary>
    /// A floor and a ceiling on CalendarAgendaDays, which is operator-typed
    /// free text in SiteSettings. Zero would sync nothing at all, and a large
    /// number would quietly turn a glanceable panel into a full calendar fetch
    /// against every connected account every five minutes.
    /// </summary>
    private const int MinDays = 1;
    private const int MaxDays = 14;

    public static int ClampDays(int days) => Math.Clamp(days, MinDays, MaxDays);

    /// <summary>
    /// IANA ids resolve on Windows too from .NET 6 on, which matters because
    /// the legacy production host is Windows (docs/delivery-architecture.md).
    /// A typo'd or unknown id degrades to UTC rather than throwing out of the
    /// caller - one wrong-looking agenda beats no agenda at all.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string id, ILogger logger)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning(ex, "Unknown site timezone {TimeZone}; resolving calendar dates in UTC instead", id);
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Local "today" in the site's timezone - the first day of the window.</summary>
    public static DateOnly Today(DateTimeOffset nowUtc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime);
}
