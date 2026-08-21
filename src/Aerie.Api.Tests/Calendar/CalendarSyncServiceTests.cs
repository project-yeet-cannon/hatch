using Aerie.Api.Ef;
using Aerie.Api.Services.Calendar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Calendar;

/// <summary>
/// Covers the two things CalendarSyncService can get wrong in ways nobody
/// notices until the wall is wrong: the date math that decides which local day
/// an event lands on, and the reconciliation that decides which cached rows
/// survive. Both have a failure mode that looks like working software - an
/// event one day off, or a day silently emptied because Google was busy.
/// </summary>
public class CalendarSyncServiceTests
{
    private const string NewYork = "America/New_York";

    /// <summary>Mid-morning in New York on a day nowhere near a DST boundary, so a test that isn't about DST isn't accidentally about DST.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 8, 21);
    private static readonly DateOnly Tomorrow = new(2026, 8, 22);

    [Fact]
    public async Task AsksTheProviderForExactlyTheAgendaWindow()
    {
        var (service, _, harness) = NewService(calendars: [Included("family@example.com")]);

        await service.SyncAsync(CancellationToken.None);

        var call = Assert.Single(harness.Client.EventCalls);
        Assert.Equal("family@example.com", call.CalendarId);
        // Local midnight, not UTC midnight: New York is four hours behind in
        // August, so "today" starts at 04:00Z and two days later ends at 04:00Z.
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 4, 0, 0, TimeSpan.Zero), call.TimeMin);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 4, 0, 0, TimeSpan.Zero), call.TimeMax);
    }

    [Fact]
    public async Task ExcludedCalendarsAreNeverFetched()
    {
        var (service, _, harness) = NewService(calendars: [Included("family@example.com"), Excluded("work@example.com")]);

        await service.SyncAsync(CancellationToken.None);

        Assert.Equal(["family@example.com"], harness.Client.EventCalls.Select(c => c.CalendarId));
    }

    [Fact]
    public async Task AllDayEventLandsOnTheDayTheProviderStated_AcrossADstBoundary()
    {
        // November 1st 2026 is the day New York falls back an hour. Resolving
        // the local day by converting UTC midnight would put this event on
        // October 31st - the classic off-by-one-for-half-the-year bug.
        var (service, db, _) = NewService(
            now: new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
            calendars: [Included("family@example.com")],
            events: [AllDay("halloween-cleanup", "Cleanup", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2))]);

        await service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Events(db));
        Assert.True(stored.IsAllDay);
        Assert.Equal(new DateOnly(2026, 11, 1), stored.LocalStartDate);
        // The provider's end is exclusive, so a one-day event ends the day it started.
        Assert.Equal(new DateOnly(2026, 11, 1), stored.LocalEndDate);
        // The day's real bounds: it begins at 04:00Z on EDT and runs 25 hours,
        // ending at 05:00Z once the clocks have gone back.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), stored.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero), stored.EndsAt);
    }

    [Fact]
    public async Task AllDayEventStartsAtTheFirstInstantThatExists_WhenTheZoneSkipsMidnight()
    {
        // Santiago springs forward *at* midnight, so 2026-09-06T00:00 local is
        // a local time that never happens. Converting it naively throws; the
        // day has to start at the first instant that does exist.
        var (service, db, _) = NewService(
            timeZone: "America/Santiago",
            now: new DateTimeOffset(2026, 9, 6, 15, 0, 0, TimeSpan.Zero),
            calendars: [Included("family@example.com")],
            events: [AllDay("spring-forward", "Fiesta", new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 7))]);

        await service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Events(db));
        Assert.Equal(new DateOnly(2026, 9, 6), stored.LocalStartDate);
        Assert.Equal(new DateOnly(2026, 9, 6), stored.LocalEndDate);
        // 00:00 local doesn't exist; 01:00 (UTC-3) does, and it is the same
        // instant the transition happens.
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 4, 0, 0, TimeSpan.Zero), stored.StartsAt);
    }

    [Fact]
    public async Task MultiDayAllDayEventSpansToTheExclusiveEndMinusOneDay()
    {
        var (service, db, _) = NewService(
            calendars: [Included("family@example.com")],
            events: [AllDay("trip", "Beach trip", Today, new DateOnly(2026, 8, 24))]);

        await service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Events(db));
        Assert.Equal(Today, stored.LocalStartDate);
        // Google says the trip ends on the 24th; the last day of it is the 23rd.
        Assert.Equal(new DateOnly(2026, 8, 23), stored.LocalEndDate);
    }

    [Fact]
    public async Task TimedEventEndingAtMidnightStaysOnTheDayItStarted()
    {
        var (service, db, _) = NewService(
            calendars: [Included("family@example.com")],
            events: [Timed("late", "Movie night",
                new DateTimeOffset(2026, 8, 22, 2, 0, 0, TimeSpan.Zero),   // 22:00 on the 21st, New York
                new DateTimeOffset(2026, 8, 22, 4, 0, 0, TimeSpan.Zero))]); // midnight on the 22nd

        await service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Events(db));
        Assert.False(stored.IsAllDay);
        Assert.Equal(Today, stored.LocalStartDate);
        // Ending *at* midnight is the end of that day, not a second day with a
        // zero-length entry on it.
        Assert.Equal(Today, stored.LocalEndDate);
    }

    [Fact]
    public async Task TimedEventRunningPastMidnightCoversBothDays()
    {
        var (service, db, _) = NewService(
            calendars: [Included("family@example.com")],
            events: [Timed("overnight", "Red-eye",
                new DateTimeOffset(2026, 8, 22, 2, 0, 0, TimeSpan.Zero),   // 22:00 on the 21st
                new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero))]); // 06:00 on the 22nd

        await service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Events(db));
        Assert.Equal(Today, stored.LocalStartDate);
        Assert.Equal(Tomorrow, stored.LocalEndDate);
    }

    [Fact]
    public async Task ReSyncUpdatesInPlaceRatherThanDuplicating()
    {
        var (service, db, harness) = NewService(
            calendars: [Included("family@example.com")],
            events: [Timed("dentist", "Dentist", Now.AddHours(2), Now.AddHours(3))]);

        await service.SyncAsync(CancellationToken.None);

        harness.SetEvents([Timed("dentist", "Dentist (moved)", Now.AddHours(5), Now.AddHours(6), location: "Main St")]);
        await service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Events(db));
        Assert.Equal("Dentist (moved)", stored.Title);
        Assert.Equal("Main St", stored.Location);
        Assert.Equal(Now.AddHours(5), stored.StartsAt);
    }

    [Fact]
    public async Task EventTheProviderNoLongerReturnsIsDeleted()
    {
        var (service, db, harness) = NewService(
            calendars: [Included("family@example.com")],
            events: [
                Timed("dentist", "Dentist", Now.AddHours(2), Now.AddHours(3)),
                Timed("soccer", "Soccer", Now.AddHours(4), Now.AddHours(5)),
            ]);

        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(2, (await Events(db)).Count);

        harness.SetEvents([Timed("soccer", "Soccer", Now.AddHours(4), Now.AddHours(5))]);
        await service.SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Events(db));
        Assert.Equal("soccer", stored.ProviderEventId);
    }

    [Fact]
    public async Task CancelledInstancesAreNeitherStoredNorKept()
    {
        var (service, db, harness) = NewService(
            calendars: [Included("family@example.com")],
            events: [Timed("soccer", "Soccer", Now.AddHours(4), Now.AddHours(5))]);

        await service.SyncAsync(CancellationToken.None);
        Assert.Single(await Events(db));

        // A cancelled instance of a recurring series comes back as a tombstone
        // rather than simply going missing.
        harness.SetEvents([Timed("soccer", "Soccer", Now.AddHours(4), Now.AddHours(5), status: "cancelled")]);
        await service.SyncAsync(CancellationToken.None);

        Assert.Empty(await Events(db));
    }

    [Fact]
    public async Task AFailedFetchLeavesTheCachedDayAlone()
    {
        var (service, db, harness) = NewService(
            calendars: [Included("family@example.com")],
            events: [Timed("soccer", "Soccer", Now.AddHours(4), Now.AddHours(5))]);

        await service.SyncAsync(CancellationToken.None);

        // The trap this guards: a failure is an empty list on the wire, and
        // reconciling against it would blank the day because Google was busy.
        harness.FailWith("http_503");
        var result = await service.SyncAsync(CancellationToken.None);

        Assert.Single(await Events(db));
        Assert.Equal(0, result.Calendars);
        var account = await Account(db);
        Assert.Contains("http_503", account.LastSyncError);
        Assert.Equal(Now, account.LastSyncedAt);
    }

    [Fact]
    public async Task ASuccessfulSyncRetiresTheLastFailure()
    {
        var (service, db, harness) = NewService(
            seedAccount: a => a.LastSyncError = "Sync failed: something older",
            calendars: [Included("family@example.com")],
            events: [Timed("soccer", "Soccer", Now.AddHours(4), Now.AddHours(5))]);

        await service.SyncAsync(CancellationToken.None);

        var account = await Account(db);
        Assert.Null(account.LastSyncError);
        Assert.Equal(Now, account.LastSyncedAt);
        Assert.Equal(harness.Time.GetUtcNow(), Assert.Single(await Events(db)).FetchedAt);
    }

    [Fact]
    public async Task AThrowingAccountLeavesTheOtherAccountsEventsIntact()
    {
        var options = NewOptions();
        var broken = Seed(options, "broken@example.com", [Included("broken-calendar")]);
        var healthy = Seed(options, "healthy@example.com", [Included("healthy-calendar")]);

        var client = new StubGoogleCalendarClient(eventsFor: calendarId => calendarId == "broken-calendar"
            ? throw new HttpRequestException("the socket went away")
            : ProviderListResult<ProviderEvent>.Ok([Timed("soccer", "Soccer", Now.AddHours(4), Now.AddHours(5))]));

        var db = new AerieContext(options);
        var service = NewService(db, client, new FakeTimeProvider(Now));

        var result = await service.SyncAsync(CancellationToken.None);

        Assert.Equal(1, result.FailedAccounts);
        Assert.Equal(1, result.Accounts);

        var stored = Assert.Single(await Events(db));
        Assert.Equal("soccer", stored.ProviderEventId);

        var brokenAccount = await Account(db, broken);
        Assert.Contains("the socket went away", brokenAccount.LastSyncError);
        // Failure or not, "last synced" answers "when did Aerie last try".
        Assert.Equal(Now, brokenAccount.LastSyncedAt);
        Assert.Null((await Account(db, healthy)).LastSyncError);
    }

    [Fact]
    public async Task DisabledAndUnauthorizedAccountsAreNotFetched()
    {
        var options = NewOptions();
        Seed(options, "paused@example.com", [Included("paused-calendar")], a => a.Enabled = false);
        Seed(options, "expired@example.com", [Included("expired-calendar")], a => a.NeedsReauth = true);

        var client = new StubGoogleCalendarClient();
        var service = NewService(new AerieContext(options), client, new FakeTimeProvider(Now));

        var result = await service.SyncAsync(CancellationToken.None);

        Assert.Empty(client.EventCalls);
        Assert.Equal(0, result.Accounts);
    }

    [Fact]
    public async Task EventsThatFellOutOfTheWindowArePruned()
    {
        var (service, db, _) = NewService(
            calendars: [Included("family@example.com")],
            // Still current, so it comes back from the fetch and is upserted -
            // the control against a prune that is simply too greedy.
            events: [AllDay("tomorrow", "Swim lessons", Tomorrow, Tomorrow.AddDays(1))]);
        await Insert(db, "family@example.com", Stale("last-week", new DateOnly(2026, 8, 14)));
        await Insert(db, "family@example.com", Stale("next-week", new DateOnly(2026, 8, 30)));
        await Insert(db, "family@example.com", Stale("tomorrow", Tomorrow));

        var result = await service.SyncAsync(CancellationToken.None);

        // Last week is what this exists to stop: an account that stops syncing
        // must not leave a dead day on the wall. Next week is the other half -
        // the window is a window at both ends.
        Assert.Equal(["tomorrow"], (await Events(db)).Select(e => e.ProviderEventId));
        Assert.Equal(2, result.Removed);
    }

    [Fact]
    public async Task EventsOnACalendarTheAdminTurnedOffArePruned()
    {
        var (service, db, _) = NewService(calendars: [Excluded("work@example.com")]);
        await Insert(db, "work@example.com", Stale("standup", Today));

        await service.SyncAsync(CancellationToken.None);

        // Un-including a calendar has to clear the wall now, not whenever its
        // events happen to age out.
        Assert.Empty(await Events(db));
    }

    [Fact]
    public async Task EventsOnADisabledAccountArePruned()
    {
        var options = NewOptions();
        Seed(options, "paused@example.com", [Included("paused-calendar")], a => a.Enabled = false);

        var db = new AerieContext(options);
        await Insert(db, "paused-calendar", Stale("standup", Today));

        await NewService(db, new StubGoogleCalendarClient(), new FakeTimeProvider(Now)).SyncAsync(CancellationToken.None);

        Assert.Empty(await Events(db));
    }

    // ---- fixtures -----------------------------------------------------------

    private static EfCalendar Included(string providerCalendarId) =>
        new() { ProviderCalendarId = providerCalendarId, Name = providerCalendarId, Included = true };

    private static EfCalendar Excluded(string providerCalendarId) =>
        new() { ProviderCalendarId = providerCalendarId, Name = providerCalendarId, Included = false };

    private static ProviderEvent AllDay(string id, string title, DateOnly start, DateOnly endExclusive) =>
        new(id, title, null, "confirmed", new ProviderEventTime(null, start), new ProviderEventTime(null, endExclusive));

    private static ProviderEvent Timed(
        string id, string title, DateTimeOffset start, DateTimeOffset end, string? location = null, string status = "confirmed") =>
        new(id, title, location, status, new ProviderEventTime(start, null), new ProviderEventTime(end, null));

    /// <summary>A cached row the provider was never asked about, for the prune tests.</summary>
    private static EfCalendarEvent Stale(string providerEventId, DateOnly day) => new()
    {
        ProviderEventId = providerEventId,
        Title = providerEventId,
        StartsAt = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
        EndsAt = day.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Utc),
        LocalStartDate = day,
        LocalEndDate = day,
        FetchedAt = Now.AddDays(-1),
    };

    /// <summary>The pieces of a run a test reaches back into: what the provider will answer, and the clock.</summary>
    private sealed class Harness(StubGoogleCalendarClient client, FakeTimeProvider time, Action<ProviderListResult<ProviderEvent>> setAnswer)
    {
        public StubGoogleCalendarClient Client => client;

        public FakeTimeProvider Time => time;

        public void SetEvents(IReadOnlyList<ProviderEvent> events) => setAnswer(ProviderListResult<ProviderEvent>.Ok(events));

        public void FailWith(string error) => setAnswer(ProviderListResult<ProviderEvent>.Failed(error));
    }

    private static DbContextOptions<AerieContext> NewOptions() =>
        new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static Guid Seed(
        DbContextOptions<AerieContext> options,
        string email,
        IReadOnlyList<EfCalendar> calendars,
        Action<EfCalendarAccount>? seedAccount = null)
    {
        var account = new EfCalendarAccount
        {
            Provider = CalendarProviders.Google,
            AccountEmail = email,
            RefreshToken = CalendarTestData.Stored("stored-refresh"),
            ConnectedAt = Now.AddDays(-1),
            Calendars = [.. calendars],
        };
        seedAccount?.Invoke(account);

        using var db = new AerieContext(options);
        db.CalendarAccounts.Add(account);
        db.SaveChanges();
        return account.Id;
    }

    private static CalendarSyncService NewService(
        AerieContext db, IGoogleCalendarClient client, FakeTimeProvider time, string timeZone = NewYork, int agendaDays = 2) =>
        new(db, client,
            new StubSiteSettings(timeZone: timeZone, calendarAgendaDays: agendaDays),
            time,
            NullLogger<CalendarSyncService>.Instance);

    /// <summary>One account with the given calendars, answering every event fetch with the same list until a test changes it.</summary>
    private static (CalendarSyncService Service, AerieContext Db, Harness Harness) NewService(
        DateTimeOffset? now = null,
        string timeZone = NewYork,
        int agendaDays = 2,
        Action<EfCalendarAccount>? seedAccount = null,
        IReadOnlyList<EfCalendar>? calendars = null,
        IReadOnlyList<ProviderEvent>? events = null)
    {
        var options = NewOptions();
        Seed(options, "family@example.com", calendars ?? [], seedAccount);

        var answer = ProviderListResult<ProviderEvent>.Ok(events ?? []);
        var client = new StubGoogleCalendarClient(eventsFor: _ => answer);
        var time = new FakeTimeProvider(now ?? Now);

        var db = new AerieContext(options);
        return (NewService(db, client, time, timeZone, agendaDays), db,
            new Harness(client, time, updated => answer = updated));
    }

    /// <summary>Adds a cached row directly, bypassing the sync, so a prune test can stage what a previous run left behind.</summary>
    private static async Task Insert(AerieContext db, string providerCalendarId, EfCalendarEvent e)
    {
        e.CalendarId = (await db.Calendars.AsNoTracking().SingleAsync(c => c.ProviderCalendarId == providerCalendarId)).Id;
        db.CalendarEvents.Add(e);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>Read back untracked, so an assertion sees what was saved rather than what the service still holds.</summary>
    private static async Task<List<EfCalendarEvent>> Events(AerieContext db) =>
        await db.CalendarEvents.AsNoTracking().OrderBy(e => e.ProviderEventId).ToListAsync();

    private static async Task<EfCalendarAccount> Account(AerieContext db, Guid? id = null) =>
        id is { } known
            ? await db.CalendarAccounts.AsNoTracking().SingleAsync(a => a.Id == known)
            : await db.CalendarAccounts.AsNoTracking().SingleAsync();
}
