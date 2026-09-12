using Hatch.Api.Ef;
using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Services.Calendar;

public interface ICalendarAgendaService
{
    Task<IReadOnlyList<CalendarDay>> GetAgendaAsync(CancellationToken ct);
}

/// <summary>
/// The dashboard's read path for the family calendar: purely a query over the
/// rows SyncCalendarEvents cached, per the manifest's rule that nothing in a
/// request path calls Google.
///
/// Every day in the window gets a CalendarDay even when it holds no events, so
/// the kiosk can distinguish "nothing tomorrow" from "tomorrow never synced" -
/// and a multi-day event appears on each day it covers, because an event that
/// started yesterday is still today's news.
/// </summary>
public class CalendarAgendaService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISiteSettingsService siteSettings,
    TimeProvider time,
    ILogger<CalendarAgendaService> logger) : ICalendarAgendaService
{
    public async Task<IReadOnlyList<CalendarDay>> GetAgendaAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var tz = CalendarWindow.ResolveTimeZone(settings.TimeZone, logger);
        var days = CalendarWindow.ClampDays(settings.CalendarAgendaDays);
        var start = CalendarWindow.Today(time.GetUtcNow(), tz);
        var endExclusive = start.AddDays(days);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Excluded calendars and disabled accounts are filtered here rather
        // than trusted to the sync: un-including a calendar has to take its
        // events off the wall on the next poll, not on the next prune.
        var rows = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.Calendar!.Included && e.Calendar.Account!.Enabled)
            .Where(e => e.LocalEndDate >= start && e.LocalStartDate < endExclusive)
            .Select(e => new AgendaRow(
                e.Id,
                e.Calendar!.Name,
                e.Calendar.ColorOverride ?? e.Calendar.ProviderColor,
                e.Calendar.SortOrder,
                e.Title,
                e.Location,
                e.IsAllDay,
                e.StartsAt,
                e.EndsAt,
                e.LocalStartDate,
                e.LocalEndDate))
            .ToListAsync(ct);

        var agenda = new List<CalendarDay>(days);
        for (var date = start; date < endExclusive; date = date.AddDays(1))
        {
            var onDay = rows
                .Where(r => r.LocalStartDate <= date && date <= r.LocalEndDate)
                // All-day first: it frames the day rather than sitting at
                // whatever hour its synthetic start happens to fall on.
                .OrderByDescending(r => r.IsAllDay)
                .ThenBy(r => r.StartsAt)
                .ThenBy(r => r.CalendarSortOrder)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Select(r => new CalendarEventSummary(
                    r.Id, r.CalendarName, r.Color, r.Title, r.Location, r.IsAllDay, r.StartsAt, r.EndsAt))
                .ToList();
            agenda.Add(new CalendarDay(date, onDay));
        }

        return agenda;
    }

    /// <summary>One event flattened with the calendar fields the grouping and ordering need, so the join is paid once per row rather than per day.</summary>
    private record AgendaRow(
        Guid Id,
        string CalendarName,
        string? Color,
        int CalendarSortOrder,
        string Title,
        string? Location,
        bool IsAllDay,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        DateOnly LocalStartDate,
        DateOnly LocalEndDate);
}
