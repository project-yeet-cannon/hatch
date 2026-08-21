using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Calendar;

/// <summary>
/// What one discovery run changed, or why it changed nothing. The counts are
/// for the log line and the admin page's toast; Error is what a caller checks.
/// </summary>
public record CalendarDiscoveryResult(int Added, int Updated, int Removed, string? Error)
{
    /// <summary>The one error a caller distinguishes: there is no such account, which is a 404 rather than a provider failure.</summary>
    public const string UnknownAccount = "unknown_account";

    public bool Succeeded => Error is null;

    public static CalendarDiscoveryResult Failed(string error) => new(0, 0, 0, error);
}

public interface ICalendarDiscoveryService
{
    /// <summary>
    /// Brings EfCalendar rows in line with what the account can currently see.
    /// Never throws: a failure comes back on the result and is recorded on the
    /// account, the same fail-soft contract the rest of Services/Calendar keeps.
    /// </summary>
    Task<CalendarDiscoveryResult> SyncCalendarListAsync(Guid accountId, CancellationToken ct);
}

/// <summary>
/// Reconciles one account's calendar list against the provider's.
///
/// The division of ownership is the whole point of this class: the provider
/// owns what a calendar *is* (name, color, timezone, primary), and the admin
/// owns what Aerie *does* with it (Included, ColorOverride, SortOrder). A
/// re-sync therefore refreshes the first set and never touches the second -
/// otherwise "Refresh calendars" would silently un-toggle a wall display.
/// </summary>
public class CalendarDiscoveryService(
    AerieContext db,
    IGoogleCalendarClient client,
    ILogger<CalendarDiscoveryService> logger) : ICalendarDiscoveryService
{
    public async Task<CalendarDiscoveryResult> SyncCalendarListAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.CalendarAccounts
            .Include(a => a.Calendars)
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        if (account is null)
        {
            logger.LogWarning("No calendar account {AccountId} to discover calendars for", accountId);
            return CalendarDiscoveryResult.Failed(CalendarDiscoveryResult.UnknownAccount);
        }

        // Enabled deliberately isn't checked. It gates the *event sync job*; a
        // discovery run is something an admin asked for by hand (or the connect
        // callback did on their behalf), and refusing it would leave a paused
        // account with a calendar list nobody can refresh.
        var fetched = await client.ListCalendarsAsync(accountId, ct);
        if (!fetched.Succeeded)
        {
            // Crucially, nothing is deleted here. A failed fetch is an empty
            // list on the wire, and treating it as the truth would wipe every
            // calendar - along with the Included flags the admin set - the
            // first time Google was unreachable.
            account.LastSyncError = $"Could not list calendars: {fetched.Error}";
            await db.SaveChangesAsync(ct);
            return CalendarDiscoveryResult.Failed(fetched.Error!);
        }

        var existing = account.Calendars.ToDictionary(c => c.ProviderCalendarId);
        // A new calendar lands after everything already ordered, rather than at
        // 0 where it would jump to the top of a list the admin arranged.
        var nextSortOrder = account.Calendars.Count == 0 ? 0 : account.Calendars.Max(c => c.SortOrder) + 1;
        int added = 0, updated = 0;

        foreach (var incoming in fetched.Items)
        {
            if (existing.Remove(incoming.Id, out var calendar))
            {
                if (Apply(calendar, incoming)) updated++;
            }
            else
            {
                db.Calendars.Add(new EfCalendar
                {
                    AccountId = account.Id,
                    ProviderCalendarId = incoming.Id,
                    Name = incoming.Name,
                    ProviderColor = incoming.Color,
                    TimeZone = incoming.TimeZone,
                    IsPrimary = incoming.IsPrimary,
                    // Included stays false: connecting an account must not put a
                    // work calendar on a kitchen wall. The admin opts each in.
                    SortOrder = nextSortOrder++,
                });
                added++;
            }
        }

        // Whatever is left in `existing` was matched by nothing the provider
        // returned - unsubscribed, or deleted outright. Its cached events go
        // with it by cascade.
        var removed = existing.Values.ToList();
        if (removed.Count > 0) db.Calendars.RemoveRange(removed);

        // A successful list proves the grant works, which retires whatever
        // failure was last recorded - the same thing GoogleTokenProvider does
        // after a refresh succeeds.
        account.LastSyncError = null;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Discovered calendars for account {AccountId}: {Added} added, {Updated} updated, {Removed} removed",
            accountId, added, updated, removed.Count);

        return new CalendarDiscoveryResult(added, updated, removed.Count, null);
    }

    /// <summary>
    /// Copies the provider-owned fields onto an existing row, reporting whether
    /// anything actually moved so the log line counts changes rather than rows.
    /// Included, ColorOverride and SortOrder are absent by design - they are the
    /// admin's, and a refresh must not overwrite them.
    /// </summary>
    private static bool Apply(EfCalendar calendar, ProviderCalendar incoming)
    {
        var changed = calendar.Name != incoming.Name
            || calendar.ProviderColor != incoming.Color
            || calendar.TimeZone != incoming.TimeZone
            || calendar.IsPrimary != incoming.IsPrimary;

        calendar.Name = incoming.Name;
        calendar.ProviderColor = incoming.Color;
        calendar.TimeZone = incoming.TimeZone;
        calendar.IsPrimary = incoming.IsPrimary;

        return changed;
    }
}
