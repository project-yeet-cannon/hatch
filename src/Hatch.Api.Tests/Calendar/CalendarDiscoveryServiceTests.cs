using Hatch.Api.Ef;
using Hatch.Api.Services.Calendar;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hatch.Api.Tests.Calendar;

/// <summary>
/// Covers the ownership split CalendarDiscoveryService exists to enforce - the
/// provider owns what a calendar is, the admin owns what Hatch does with it -
/// and the one failure mode that would otherwise destroy admin data: a fetch
/// that failed being read as "this account has no calendars any more".
/// </summary>
public class CalendarDiscoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AddsEveryDiscoveredCalendar_ExcludedUntilTheAdminOptsItIn()
    {
        var (service, db, accountId) = NewService(returning: [
            Calendar("family@example.com", "Family", primary: true),
            Calendar("work@example.com", "Work"),
        ]);

        var result = await service.SyncCalendarListAsync(accountId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Added);
        var calendars = await Calendars(db, accountId);
        Assert.Equal(["family@example.com", "work@example.com"], calendars.Select(c => c.ProviderCalendarId).Order());
        // The whole reason Included defaults to false: connecting an account
        // must not put a work calendar on a kitchen wall.
        Assert.All(calendars, c => Assert.False(c.Included));
        Assert.True(calendars.Single(c => c.ProviderCalendarId == "family@example.com").IsPrimary);
    }

    [Fact]
    public async Task ResyncPreservesTheAdminsChoices_WhileRefreshingTheProvidersFields()
    {
        var (service, db, accountId) = NewService(
            seed: [Existing("family@example.com", "Old name", included: true, colorOverride: "#ff0000", sortOrder: 7)],
            returning: [Calendar("family@example.com", "Family", color: "#4285f4", timeZone: "America/New_York")]);

        var result = await service.SyncCalendarListAsync(accountId, CancellationToken.None);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Removed);

        var calendar = Assert.Single(await Calendars(db, accountId));
        // The provider's half moved.
        Assert.Equal("Family", calendar.Name);
        Assert.Equal("#4285f4", calendar.ProviderColor);
        Assert.Equal("America/New_York", calendar.TimeZone);
        // The admin's half did not.
        Assert.True(calendar.Included);
        Assert.Equal("#ff0000", calendar.ColorOverride);
        Assert.Equal(7, calendar.SortOrder);
    }

    [Fact]
    public async Task UnchangedCalendarIsNotCountedAsUpdated()
    {
        var (service, _, accountId) = NewService(
            seed: [Existing("family@example.com", "Family")],
            returning: [Calendar("family@example.com", "Family")]);

        var result = await service.SyncCalendarListAsync(accountId, CancellationToken.None);

        Assert.Equal(0, result.Updated);
    }

    [Fact]
    public async Task RemovesACalendarTheAccountCanNoLongerSee()
    {
        var (service, db, accountId) = NewService(
            seed: [Existing("family@example.com", "Family"), Existing("gone@example.com", "Unshared", included: true)],
            returning: [Calendar("family@example.com", "Family")]);

        var result = await service.SyncCalendarListAsync(accountId, CancellationToken.None);

        Assert.Equal(1, result.Removed);
        Assert.Equal("family@example.com", Assert.Single(await Calendars(db, accountId)).ProviderCalendarId);
    }

    [Fact]
    public async Task NewCalendarSortsAfterTheOnesTheAdminAlreadyOrdered()
    {
        var (service, db, accountId) = NewService(
            seed: [Existing("family@example.com", "Family", sortOrder: 3)],
            returning: [Calendar("family@example.com", "Family"), Calendar("new@example.com", "Newly shared")]);

        await service.SyncCalendarListAsync(accountId, CancellationToken.None);

        var calendars = await Calendars(db, accountId);
        Assert.Equal(3, calendars.Single(c => c.ProviderCalendarId == "family@example.com").SortOrder);
        Assert.Equal(4, calendars.Single(c => c.ProviderCalendarId == "new@example.com").SortOrder);
    }

    [Fact]
    public async Task AFailedFetchChangesNothing_RatherThanReadingEmptyAsGone()
    {
        var (service, db, accountId) = NewService(
            seed: [Existing("family@example.com", "Family", included: true, colorOverride: "#ff0000")],
            failWith: "unreachable");

        var result = await service.SyncCalendarListAsync(accountId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("unreachable", result.Error);

        // The admin's toggles survive a Google outage - this is the assertion
        // the ProviderListResult error channel exists for.
        var calendar = Assert.Single(await Calendars(db, accountId));
        Assert.True(calendar.Included);
        Assert.Equal("#ff0000", calendar.ColorOverride);
        Assert.Contains("unreachable", (await Account(db, accountId)).LastSyncError);
    }

    [Fact]
    public async Task ASuccessfulRunClearsAPreviousError()
    {
        var (service, db, accountId) = NewService(
            account => account.LastSyncError = "Could not list calendars: unreachable",
            returning: [Calendar("family@example.com", "Family")]);

        await service.SyncCalendarListAsync(accountId, CancellationToken.None);

        Assert.Null((await Account(db, accountId)).LastSyncError);
    }

    [Fact]
    public async Task RunsForADisabledAccount_BecauseEnabledGatesTheSyncJobNotDiscovery()
    {
        var (service, db, accountId) = NewService(
            account => account.Enabled = false,
            returning: [Calendar("family@example.com", "Family")]);

        Assert.True((await service.SyncCalendarListAsync(accountId, CancellationToken.None)).Succeeded);
        Assert.Single(await Calendars(db, accountId));
    }

    [Fact]
    public async Task UnknownAccountFails_WithoutCallingTheProvider()
    {
        var (service, _, _) = NewService();

        var result = await service.SyncCalendarListAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("unknown_account", result.Error);
    }

    private static ProviderCalendar Calendar(
        string id, string name, string? color = null, string? timeZone = null, bool primary = false) =>
        new(id, name, color, timeZone, primary);

    private static EfCalendar Existing(
        string providerCalendarId, string name, bool included = false, string? colorOverride = null, int sortOrder = 0) =>
        new()
        {
            ProviderCalendarId = providerCalendarId,
            Name = name,
            Included = included,
            ColorOverride = colorOverride,
            SortOrder = sortOrder,
        };

    private static (CalendarDiscoveryService Service, AppDbContext Db, Guid AccountId) NewService(
        Action<EfCalendarAccount>? seedAccount = null,
        IReadOnlyList<EfCalendar>? seed = null,
        IReadOnlyList<ProviderCalendar>? returning = null,
        string? failWith = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var account = new EfCalendarAccount
        {
            Provider = CalendarProviders.Google,
            AccountEmail = "family@example.com",
            RefreshToken = CalendarTestData.Stored("stored-refresh"),
            ConnectedAt = Now.AddDays(-1),
            Calendars = [.. seed ?? []],
        };
        seedAccount?.Invoke(account);

        using (var seedDb = new AppDbContext(options))
        {
            seedDb.CalendarAccounts.Add(account);
            seedDb.SaveChanges();
        }

        var client = failWith is null
            ? new StubGoogleCalendarClient(ProviderListResult<ProviderCalendar>.Ok(returning ?? []))
            : new StubGoogleCalendarClient(ProviderListResult<ProviderCalendar>.Failed(failWith));

        var db = new AppDbContext(options);
        return (new CalendarDiscoveryService(db, client, NullLogger<CalendarDiscoveryService>.Instance), db, account.Id);
    }

    /// <summary>Reads back untracked, so an assertion sees what was saved rather than what the service still holds in memory.</summary>
    private static async Task<List<EfCalendar>> Calendars(AppDbContext db, Guid accountId) =>
        await db.Calendars.AsNoTracking().Where(c => c.AccountId == accountId).ToListAsync();

    private static async Task<EfCalendarAccount> Account(AppDbContext db, Guid id) =>
        await db.CalendarAccounts.AsNoTracking().SingleAsync(a => a.Id == id);
}
