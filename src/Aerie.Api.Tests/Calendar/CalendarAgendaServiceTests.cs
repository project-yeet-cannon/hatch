using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.Calendar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Calendar;

/// <summary>
/// Covers the shaping the kiosk depends on: which local day a cached row lands
/// on, that an empty day still appears, the ordering within a day, and which
/// of a calendar's two colors wins. Every failure here looks like working
/// software on the wall - a day missing, an event on the wrong one, or a
/// recolored calendar that stayed the provider's color.
/// </summary>
public class CalendarAgendaServiceTests
{
    private const string NewYork = "America/New_York";

    /// <summary>Mid-morning in New York, nowhere near a DST boundary.</summary>
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 8, 21);
    private static readonly DateOnly Tomorrow = new(2026, 8, 22);

    [Fact]
    public async Task GroupsAnEventOntoItsLocalDate()
    {
        var factory = await Seed(cal => cal.Events =
        [
            Timed("dentist", Today, 9, 10),
            Timed("soccer", Tomorrow, 17, 18),
        ]);

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        Assert.Equal([Today, Tomorrow], agenda.Select(d => d.Date));
        Assert.Equal(["dentist"], Titles(agenda[0]));
        Assert.Equal(["soccer"], Titles(agenda[1]));
    }

    [Fact]
    public async Task EmptyDaysSurvive()
    {
        var factory = await Seed(cal => cal.Events = [Timed("dentist", Today, 9, 10)]);

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        // Two CalendarDays for CalendarAgendaDays=2, the second one empty: the
        // kiosk needs "nothing tomorrow" to be renderable.
        Assert.Equal(2, agenda.Count);
        Assert.Equal(Tomorrow, agenda[1].Date);
        Assert.Empty(agenda[1].Events);
    }

    [Fact]
    public async Task AllDayEventsSortAheadOfTimedOnes()
    {
        var factory = await Seed(cal => cal.Events =
        [
            Timed("breakfast", Today, 8, 9),
            AllDay("vacation", Today, Today),
        ]);

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        // Not the order they were stored in, and not their StartsAt order
        // either - the all-day row starts at local midnight and would sort
        // first here by accident, so the timed event is deliberately the
        // earlier-inserted one.
        Assert.Equal(["vacation", "breakfast"], Titles(agenda[0]));
    }

    [Fact]
    public async Task TimedEventsSortByStart()
    {
        var factory = await Seed(cal => cal.Events =
        [
            Timed("dinner", Today, 18, 19),
            Timed("lunch", Today, 12, 13),
            Timed("breakfast", Today, 8, 9),
        ]);

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        Assert.Equal(["breakfast", "lunch", "dinner"], Titles(agenda[0]));
    }

    [Fact]
    public async Task MultiDayEventAppearsOnEveryDayItCovers()
    {
        var factory = await Seed(cal => cal.Events = [AllDay("vacation", Today, Tomorrow)]);

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        Assert.Equal(["vacation"], Titles(agenda[0]));
        Assert.Equal(["vacation"], Titles(agenda[1]));
    }

    [Fact]
    public async Task EventStartedBeforeTheWindowStillShowsOnTodayWhenItRunsThrough()
    {
        var factory = await Seed(cal => cal.Events = [AllDay("vacation", Today.AddDays(-3), Today)]);

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        Assert.Equal(["vacation"], Titles(agenda[0]));
    }

    [Fact]
    public async Task ColorOverrideWinsOverProviderColor()
    {
        var factory = await Seed(cal =>
        {
            cal.ProviderColor = "#0000ff";
            cal.ColorOverride = "#ff0000";
            cal.Events = [Timed("dentist", Today, 9, 10)];
        });

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        Assert.Equal("#ff0000", agenda[0].Events[0].Color);
    }

    [Fact]
    public async Task ProviderColorIsUsedWhenTheAdminSetNoOverride()
    {
        var factory = await Seed(cal =>
        {
            cal.ProviderColor = "#0000ff";
            cal.Events = [Timed("dentist", Today, 9, 10)];
        });

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        Assert.Equal("#0000ff", agenda[0].Events[0].Color);
    }

    [Fact]
    public async Task ExcludedCalendarsAreLeftOff()
    {
        var factory = await Seed(cal =>
        {
            cal.Included = false;
            cal.Events = [Timed("standup", Today, 9, 10)];
        });

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        // The rows are still cached until the sync prunes them; un-including a
        // calendar has to take it off the wall on the next poll regardless.
        Assert.All(agenda, day => Assert.Empty(day.Events));
    }

    [Fact]
    public async Task DisabledAccountsAreLeftOff()
    {
        var factory = await Seed(
            cal => cal.Events = [Timed("standup", Today, 9, 10)],
            account => account.Enabled = false);

        var agenda = await NewService(factory).GetAgendaAsync(CancellationToken.None);

        Assert.All(agenda, day => Assert.Empty(day.Events));
    }

    [Fact]
    public async Task WindowLengthFollowsCalendarAgendaDays()
    {
        var factory = await Seed(cal => cal.Events = []);

        var agenda = await NewService(factory, agendaDays: 5).GetAgendaAsync(CancellationToken.None);

        Assert.Equal(5, agenda.Count);
        Assert.Equal(Today.AddDays(4), agenda[^1].Date);
    }

    [Fact]
    public async Task AnAgendaDaysOfZeroStillRendersToday()
    {
        // CalendarAgendaDays is operator-typed, and the clamp lives in
        // CalendarWindow so this window can never be wider or narrower than
        // the one the sync job cached.
        var factory = await Seed(cal => cal.Events = [Timed("dentist", Today, 9, 10)]);

        var agenda = await NewService(factory, agendaDays: 0).GetAgendaAsync(CancellationToken.None);

        Assert.Single(agenda);
        Assert.Equal(["dentist"], Titles(agenda[0]));
    }

    private static IReadOnlyList<string> Titles(CalendarDay day) => [.. day.Events.Select(e => e.Title)];

    private static CalendarAgendaService NewService(
        IDbContextFactory<AerieContext> factory, string timeZone = NewYork, int agendaDays = 2) =>
        new(factory,
            new StubSiteSettings(timeZone: timeZone, calendarAgendaDays: agendaDays),
            new FakeTimeProvider(Now),
            NullLogger<CalendarAgendaService>.Instance);

    /// <summary>One enabled account with one included calendar, whose rows the test stages directly - this service never syncs.</summary>
    private static async Task<IDbContextFactory<AerieContext>> Seed(
        Action<EfCalendar> seedCalendar, Action<EfCalendarAccount>? seedAccount = null)
    {
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var calendar = new EfCalendar
        {
            ProviderCalendarId = "family@example.com",
            Name = "Family",
            Included = true,
        };
        seedCalendar(calendar);

        var account = new EfCalendarAccount
        {
            Provider = CalendarProviders.Google,
            AccountEmail = "family@example.com",
            RefreshToken = CalendarTestData.Stored("stored-refresh"),
            ConnectedAt = Now.AddDays(-1),
            Calendars = [calendar],
        };
        seedAccount?.Invoke(account);

        await using var db = await factory.CreateDbContextAsync();
        db.CalendarAccounts.Add(account);
        await db.SaveChangesAsync();
        return factory;
    }

    /// <summary>A timed event on a local day, stored the way CalendarSyncService stores one (UTC instants plus the resolved local dates).</summary>
    private static EfCalendarEvent Timed(string title, DateOnly day, int startHour, int endHour) => new()
    {
        ProviderEventId = title,
        Title = title,
        StartsAt = LocalHour(day, startHour),
        EndsAt = LocalHour(day, endHour),
        LocalStartDate = day,
        LocalEndDate = day,
        FetchedAt = Now,
    };

    private static EfCalendarEvent AllDay(string title, DateOnly start, DateOnly endInclusive) => new()
    {
        ProviderEventId = title,
        Title = title,
        IsAllDay = true,
        StartsAt = LocalHour(start, 0),
        EndsAt = LocalHour(endInclusive.AddDays(1), 0),
        LocalStartDate = start,
        LocalEndDate = endInclusive,
        FetchedAt = Now,
    };

    /// <summary>New York is four hours behind UTC in August, which is the only date arithmetic these fixtures need.</summary>
    private static DateTimeOffset LocalHour(DateOnly day, int hour) =>
        new DateTimeOffset(day.ToDateTime(new TimeOnly(0, 0)), TimeSpan.FromHours(-4)).AddHours(hour);

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
        public Task<AerieContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AerieContext(options));
    }
}
