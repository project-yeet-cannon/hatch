using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Calendar;

/// <summary>
/// What one sync run did, for the log line and for the admin page's "Sync now"
/// toast. Accounts that failed are counted rather than surfaced individually -
/// each one's message is on its own row as LastSyncError, which is where the
/// page reads it from anyway.
/// </summary>
public record CalendarSyncResult(int Accounts, int Calendars, int Written, int Removed, int FailedAccounts);

public interface ICalendarSyncService
{
    /// <summary>
    /// Brings the cached events for every included calendar in line with the
    /// provider, for the agenda window only. Never throws: one broken account
    /// records its failure and the rest still sync.
    /// </summary>
    Task<CalendarSyncResult> SyncAsync(CancellationToken ct);
}

/// <summary>
/// The fetch half of the calendar's fetch/cache/read split
/// (docs/plans/kiosk.md): this talks to Google and writes Postgres, and
/// GET /api/dashboard reads Postgres and never talks to Google.
///
/// It caches a *window*, not a calendar. Only today through
/// today + CalendarAgendaDays is stored, so the table stays the size of what
/// the kiosk can display no matter how busy the family is, and an account that
/// stops syncing ages out of the wall instead of freezing last week onto it.
///
/// The date math is the part worth reading twice. Google states an all-day
/// event as bare calendar dates with an *exclusive* end, and a timed event as
/// instants; the kiosk wants "which local day is this on". Resolving that needs
/// the site timezone, and getting it wrong is how an all-day event lands one
/// day off for half the year - hence CalendarSyncServiceTests.
/// </summary>
public class CalendarSyncService(
    AerieContext db,
    IGoogleCalendarClient client,
    ISiteSettingsService siteSettings,
    TimeProvider time,
    ILogger<CalendarSyncService> logger) : ICalendarSyncService
{
    public async Task<CalendarSyncResult> SyncAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var tz = CalendarWindow.ResolveTimeZone(settings.TimeZone, logger);

        var days = CalendarWindow.ClampDays(settings.CalendarAgendaDays);
        var today = CalendarWindow.Today(time.GetUtcNow(), tz);
        // Exclusive, like the provider's own all-day end and like the query
        // bound it becomes: "two days" is today and tomorrow, not three days.
        var endExclusive = today.AddDays(days);

        var window = new SyncWindow(tz, today, endExclusive, LocalMidnightUtc(today, tz), LocalMidnightUtc(endExclusive, tz));

        // Ids first, then one account loaded per iteration. Both halves matter:
        // the change tracker stays the size of a single account no matter how
        // many are connected, and a failure can clear it without detaching an
        // account that hasn't been synced yet.
        //
        // NeedsReauth is filtered here rather than skipped in the loop because
        // GoogleTokenProvider would refuse such an account anyway, once per
        // included calendar - and a dead grant would write that warning on a
        // five-minute loop until somebody reconnected.
        var accountIds = await db.CalendarAccounts
            .Where(a => a.Enabled && !a.NeedsReauth)
            .OrderBy(a => a.AccountEmail)
            .Select(a => a.Id)
            .ToListAsync(ct);

        int syncedAccounts = 0, syncedCalendars = 0, written = 0, removed = 0, failed = 0;

        foreach (var accountId in accountIds)
        {
            db.ChangeTracker.Clear();

            var account = await db.CalendarAccounts
                .Include(a => a.Calendars)
                .FirstOrDefaultAsync(a => a.Id == accountId, ct);

            // Disconnected between the two queries, which is a race the admin
            // won: there is nothing left to sync.
            if (account is null) continue;

            try
            {
                var counts = await SyncAccountAsync(account, window, ct);
                syncedAccounts++;
                syncedCalendars += counts.Calendars;
                written += counts.Written;
                removed += counts.Removed;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The whole reason each account is its own try: one household
                // member's broken grant must not cost everyone else their
                // agenda. The message lands on the row the admin page reads.
                failed++;
                logger.LogError(ex, "Syncing calendar account {AccountId} failed", accountId);
                await RecordFailureAsync(accountId, ex, ct);
            }
        }

        // Nothing below needs the accounts, and the prune's own reads should
        // not be answered out of the last account's tracked graph.
        db.ChangeTracker.Clear();

        removed += await PruneAsync(window, ct);

        logger.LogInformation(
            "Calendar sync covered {Days} day(s) from {Start:yyyy-MM-dd} in {TimeZone}: {Accounts} account(s), {Calendars} calendar(s), {Written} event(s) written, {Removed} removed, {Failed} account(s) failed",
            days, window.Start, tz.Id, syncedAccounts, syncedCalendars, written, removed, failed);

        return new CalendarSyncResult(syncedAccounts, syncedCalendars, written, removed, failed);
    }

    /// <summary>
    /// One account's included calendars. Errors from the provider come back on
    /// the result rather than as exceptions, so this accumulates the first one
    /// onto the account instead of letting a single unreachable calendar be
    /// reported as the whole account failing.
    /// </summary>
    private async Task<(int Calendars, int Written, int Removed)> SyncAccountAsync(
        EfCalendarAccount account, SyncWindow window, CancellationToken ct)
    {
        var included = account.Calendars.Where(c => c.Included).ToList();
        int calendars = 0, written = 0, removed = 0;
        string? firstError = null;

        foreach (var calendar in included)
        {
            var fetched = await client.ListEventsAsync(
                account.Id, calendar.ProviderCalendarId, window.StartUtc, window.EndUtc, ct);

            if (!fetched.Succeeded)
            {
                // Same trap CalendarDiscoveryService guards: a failed fetch is
                // an empty list on the wire, and reconciling against it would
                // delete a calendar's whole day because Google was busy.
                firstError ??= $"Could not list events for {calendar.Name}: {fetched.Error}";
                logger.LogWarning(
                    "Could not list events for calendar {CalendarId} on account {AccountId}: {Error}",
                    calendar.Id, account.Id, fetched.Error);
                continue;
            }

            var counts = await ReconcileCalendarAsync(calendar, fetched.Items, window, ct);
            calendars++;
            written += counts.Written;
            removed += counts.Removed;
        }

        account.LastSyncedAt = time.GetUtcNow();
        // Null on success retires whatever failure was last shown, the same way
        // a successful discovery or token refresh does.
        account.LastSyncError = firstError;
        await db.SaveChangesAsync(ct);

        return (calendars, written, removed);
    }

    /// <summary>
    /// Puts a failure on the account row, starting from a clean change tracker.
    /// Whatever threw may have left an unsavable entity behind - a duplicate
    /// event written by an on-demand sync racing a job firing, say - and
    /// recording the message on top of that would throw again, out of the very
    /// catch that exists to contain it.
    /// </summary>
    private async Task RecordFailureAsync(Guid accountId, Exception ex, CancellationToken ct)
    {
        db.ChangeTracker.Clear();

        var account = await db.CalendarAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return;

        account.LastSyncError = $"Sync failed: {ex.Message}";
        // Stamped even on failure: "last synced" answers "when did Aerie last
        // try", and a blank one next to an error message reads as never-run.
        account.LastSyncedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Makes one calendar's cached rows match the fetched instances: upsert by
    /// (CalendarId, ProviderEventId), then delete whatever the provider no
    /// longer returned for this calendar *within the window* - a row outside it
    /// belongs to PruneAsync, not to a fetch that was never asked about it.
    /// </summary>
    private async Task<(int Written, int Removed)> ReconcileCalendarAsync(
        EfCalendar calendar, IReadOnlyList<ProviderEvent> fetched, SyncWindow window, CancellationToken ct)
    {
        var existing = await db.CalendarEvents
            .Where(e => e.CalendarId == calendar.Id)
            .ToDictionaryAsync(e => e.ProviderEventId, ct);

        var now = time.GetUtcNow();
        var written = 0;

        // Keyed rather than iterated: a duplicate id across two pages would
        // otherwise become a second insert and violate the unique index.
        var incoming = fetched
            // Cancelled instances of a recurring series come back as tombstones
            // rather than as absences, so they are dropped here and then
            // deleted below as rows the response no longer covers.
            .Where(e => !string.Equals(e.Status, CancelledStatus, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.Last());

        foreach (var (id, provider) in incoming)
        {
            var resolved = Resolve(provider, window.TimeZone);
            if (resolved is null)
            {
                logger.LogWarning("Event {ProviderEventId} on calendar {CalendarId} had no usable start; skipping", id, calendar.Id);
                continue;
            }

            if (existing.Remove(id, out var row))
            {
                Apply(row, provider, resolved, now);
            }
            else
            {
                row = new EfCalendarEvent
                {
                    CalendarId = calendar.Id,
                    ProviderEventId = id,
                    Title = provider.Title,
                    StartsAt = resolved.StartsAt,
                    EndsAt = resolved.EndsAt,
                    FetchedAt = now,
                };
                Apply(row, provider, resolved, now);
                db.CalendarEvents.Add(row);
            }

            written++;
        }

        // Whatever the provider didn't return: deleted, moved out of the
        // window, or cancelled. All three mean the same thing to the wall.
        var stale = existing.Values
            .Where(e => Overlaps(e, window))
            .ToList();
        if (stale.Count > 0) db.CalendarEvents.RemoveRange(stale);

        await db.SaveChangesAsync(ct);
        return (written, stale.Count);
    }

    /// <summary>
    /// Deletes everything the kiosk can no longer display: events that fell out
    /// of the window, and events on calendars that are no longer synced at all
    /// (un-included, or on an account the operator disabled). Without this, an
    /// account that stops syncing leaves last week on the wall indefinitely.
    ///
    /// An account flagged NeedsReauth deliberately keeps its rows: they age out
    /// of the window on their own within a day or two, which is a gentler
    /// failure than a family calendar going blank the moment a token expires.
    /// </summary>
    private async Task<int> PruneAsync(SyncWindow window, CancellationToken ct)
    {
        // Loaded and removed rather than ExecuteDelete. The set is bounded by
        // one window's worth of drift - a handful of rows on a normal run - so
        // the round trip costs little, and keeping every write in this service
        // on the change-tracked path is what lets the whole reconciliation be
        // driven in tests rather than only against a real Postgres.
        var stale = await db.CalendarEvents
            .Where(e => e.LocalEndDate < window.Start
                || e.LocalStartDate >= window.EndExclusive
                || !e.Calendar!.Included
                || !e.Calendar.Account!.Enabled)
            .ToListAsync(ct);

        if (stale.Count == 0) return 0;

        db.CalendarEvents.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    /// <summary>Whether a cached row falls inside the window the fetch just covered - the only rows that fetch's silence says anything about.</summary>
    private static bool Overlaps(EfCalendarEvent e, SyncWindow window) =>
        e.LocalEndDate >= window.Start && e.LocalStartDate < window.EndExclusive;

    private static void Apply(EfCalendarEvent row, ProviderEvent provider, ResolvedTimes resolved, DateTimeOffset now)
    {
        row.Title = provider.Title;
        row.Location = provider.Location;
        row.Status = provider.Status;
        row.IsAllDay = resolved.IsAllDay;
        row.StartsAt = resolved.StartsAt;
        row.EndsAt = resolved.EndsAt;
        row.LocalStartDate = resolved.LocalStartDate;
        row.LocalEndDate = resolved.LocalEndDate;
        row.FetchedAt = now;
    }

    /// <summary>Google's spelling for an instance that has been called off. Kept as a constant because it is compared, not stored, in this class.</summary>
    private const string CancelledStatus = "cancelled";

    /// <summary>
    /// The two forms a provider states an event in, collapsed into the four
    /// fields the entity stores. Null when neither end carried anything usable,
    /// which is a malformed event rather than a kind of event.
    /// </summary>
    internal static ResolvedTimes? Resolve(ProviderEvent provider, TimeZoneInfo tz)
    {
        // All-day: bare calendar dates, no timezone math on the dates
        // themselves. The end is *exclusive* - a one-day event on the 14th ends
        // on the 15th - so the inclusive last day is one back from it.
        if (provider.Start.Date is { } startDate)
        {
            var endExclusive = provider.End.Date is { } candidate && candidate > startDate
                // A malformed or missing end (equal to the start, or absent)
                // means a single day, which is the shortest an all-day event
                // can be.
                ? candidate
                : startDate.AddDays(1);

            return new ResolvedTimes(
                IsAllDay: true,
                // The instants exist so an all-day event still sorts against
                // timed ones; they are the local day's bounds, not the dates.
                StartsAt: LocalMidnightUtc(startDate, tz),
                EndsAt: LocalMidnightUtc(endExclusive, tz),
                LocalStartDate: startDate,
                LocalEndDate: endExclusive.AddDays(-1));
        }

        if (provider.Start.Instant is not { } startsAt) return null;

        // A timed event's end can be absent (a zero-length reminder); an end
        // before its start is malformed and is clamped rather than dropped,
        // since the start is the part the family reads.
        var endsAt = provider.End.Instant is { } end && end >= startsAt ? end : startsAt;

        var localStart = TimeZoneInfo.ConvertTime(startsAt, tz);
        var localEnd = TimeZoneInfo.ConvertTime(endsAt, tz);

        var localEndDate = DateOnly.FromDateTime(localEnd.DateTime);
        // An event running to exactly midnight ends *that* day, not the next
        // one - the same exclusive-end trap as the all-day form, and the reason
        // a 22:00-00:00 event doesn't put a stray entry on tomorrow's heading.
        if (localEnd.TimeOfDay == TimeSpan.Zero && localEnd > localStart)
            localEndDate = localEndDate.AddDays(-1);

        var localStartDate = DateOnly.FromDateTime(localStart.DateTime);

        return new ResolvedTimes(
            IsAllDay: false,
            StartsAt: startsAt.ToUniversalTime(),
            EndsAt: endsAt.ToUniversalTime(),
            LocalStartDate: localStartDate,
            // Clamped so a malformed pair can never produce a backwards range,
            // which would make the row invisible to every window query.
            LocalEndDate: localEndDate < localStartDate ? localStartDate : localEndDate);
    }

    /// <summary>
    /// The UTC instant a local calendar day begins. Not a plain
    /// ConvertTimeToUtc, because a spring-forward can delete local midnight
    /// outright (Santiago, Beirut) - which throws - and a fall-back can make it
    /// happen twice, where the day starts at the first of the two.
    /// </summary>
    internal static DateTimeOffset LocalMidnightUtc(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);

        // Steps in 15 minutes because half-hour and 45-minute offsets exist
        // (Lord Howe shifts by 30). A DST gap is at most a couple of hours, so
        // this settles within a handful of iterations.
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(15);

        var offset = tz.IsAmbiguousTime(local)
            // The larger offset is the earlier instant - the first time the
            // clock read midnight, which is when the day actually started.
            ? tz.GetAmbiguousTimeOffsets(local).Max()
            : tz.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>The agenda window in both the forms it is needed: local dates for the stored day columns, UTC instants for the provider query.</summary>
    private record SyncWindow(
        TimeZoneInfo TimeZone, DateOnly Start, DateOnly EndExclusive, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    /// <summary>One provider event's times as EfCalendarEvent stores them.</summary>
    internal record ResolvedTimes(
        bool IsAllDay,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        DateOnly LocalStartDate,
        DateOnly LocalEndDate);
}
