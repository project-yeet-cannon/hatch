using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>Covers the read side of SiteSettings - defaults when a key is absent, and the deobfuscation the snapshot does for secrets - against an EF Core InMemory database.</summary>
public class SiteSettingsServiceTests
{
    private static SiteSettingsService NewService(params (string Key, string Value)[] settings)
    {
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        using (var db = factory.CreateDbContext())
        {
            db.SiteSettings.AddRange(settings.Select(s => new EfSiteSetting { Key = s.Key, Value = s.Value }));
            db.SaveChanges();
        }

        return new SiteSettingsService(factory, new FakeTimeProvider());
    }

    [Fact]
    public async Task CalendarAgendaDays_DefaultsToTwo_WhenUnset()
    {
        var snapshot = await NewService().GetAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.CalendarAgendaDays);
    }

    [Fact]
    public async Task CalendarAgendaDays_DefaultsToTwo_WhenNotAnInteger()
    {
        var snapshot = await NewService((SiteSettingKeys.CalendarAgendaDays, "a week")).GetAsync(CancellationToken.None);

        Assert.Equal(2, snapshot.CalendarAgendaDays);
    }

    [Fact]
    public async Task CalendarAgendaDays_ReadsTheStoredValue()
    {
        var snapshot = await NewService((SiteSettingKeys.CalendarAgendaDays, "5")).GetAsync(CancellationToken.None);

        Assert.Equal(5, snapshot.CalendarAgendaDays);
    }

    [Fact]
    public async Task GoogleClientSecret_RoundTripsThroughObfuscation()
    {
        var snapshot = await NewService(
                (SiteSettingKeys.GoogleClientSecret, SecretObfuscator.Obfuscate("GOCSPX-super-secret")))
            .GetAsync(CancellationToken.None);

        Assert.Equal("GOCSPX-super-secret", snapshot.GoogleClientSecret);
    }

    [Fact]
    public async Task GoogleClientSecret_IsNull_WhenTheStoredValueIsNotObfuscatedText()
    {
        // A hand-edited row shouldn't throw out of a snapshot every settings
        // read in the app depends on - it degrades to "unset" instead.
        var snapshot = await NewService((SiteSettingKeys.GoogleClientSecret, "not base64!")).GetAsync(CancellationToken.None);

        Assert.Null(snapshot.GoogleClientSecret);
    }

    [Fact]
    public async Task GoogleClientIdAndRedirectUri_AreNull_WhenUnsetOrBlank()
    {
        var snapshot = await NewService((SiteSettingKeys.GoogleOAuthRedirectUri, "  ")).GetAsync(CancellationToken.None);

        Assert.Null(snapshot.GoogleClientId);
        Assert.Null(snapshot.GoogleOAuthRedirectUri);
    }

    [Fact]
    public async Task GoogleClientId_ReadsTheStoredValue()
    {
        var snapshot = await NewService((SiteSettingKeys.GoogleClientId, "123.apps.googleusercontent.com"))
            .GetAsync(CancellationToken.None);

        Assert.Equal("123.apps.googleusercontent.com", snapshot.GoogleClientId);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
        public Task<AerieContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AerieContext(options));
    }
}
