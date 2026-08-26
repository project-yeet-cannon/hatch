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
                (SiteSettingKeys.GoogleClientSecret, SecretProtector.Protect("GOCSPX-super-secret")))
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

    [Fact]
    public async Task HazardProviders_DefaultToTheTwoKeylessOnes_WhenUnset()
    {
        // Both are keyless, so an operator who never opens the settings page
        // still gets alerts - the defaults are the feature being on.
        var snapshot = await NewService().GetAsync(CancellationToken.None);

        Assert.Equal(HazardProviders.Nws, snapshot.WeatherAlertProvider);
        Assert.Equal(HazardProviders.OpenMeteo, snapshot.AirQualityProvider);
    }

    [Fact]
    public async Task HazardProviders_ReadTheStoredValue_IncludingNone()
    {
        var snapshot = await NewService(
                (SiteSettingKeys.WeatherAlertProvider, HazardProviders.None),
                (SiteSettingKeys.AirQualityProvider, "some-other-provider"))
            .GetAsync(CancellationToken.None);

        // The snapshot reports what was typed; HazardProviderResolver is what
        // decides that one of these resolves to nothing.
        Assert.Equal(HazardProviders.None, snapshot.WeatherAlertProvider);
        Assert.Equal("some-other-provider", snapshot.AirQualityProvider);
    }

    [Fact]
    public async Task HazardProviders_FallBackToTheDefault_WhenBlank()
    {
        var snapshot = await NewService((SiteSettingKeys.WeatherAlertProvider, "  ")).GetAsync(CancellationToken.None);

        Assert.Equal(HazardProviders.Nws, snapshot.WeatherAlertProvider);
    }

    [Fact]
    public async Task WeatherAlertContact_IsNull_WhenUnset()
    {
        var snapshot = await NewService().GetAsync(CancellationToken.None);

        Assert.Null(snapshot.WeatherAlertContact);
    }

    [Fact]
    public async Task WeatherAlertContact_ReadsTheStoredValue()
    {
        var snapshot = await NewService((SiteSettingKeys.WeatherAlertContact, "aerie@example.com"))
            .GetAsync(CancellationToken.None);

        Assert.Equal("aerie@example.com", snapshot.WeatherAlertContact);
    }

    [Fact]
    public async Task HazardThresholds_UseTheirDefaults_WhenUnset()
    {
        var snapshot = await NewService().GetAsync(CancellationToken.None);

        Assert.Equal(101, snapshot.AirQualityAlertThresholdAqi);
        Assert.Equal(48, snapshot.HazardMaxSeverityAgeHours);
    }

    [Fact]
    public async Task HazardThresholds_ReadStoredValuesAndIgnoreNonsense()
    {
        var snapshot = await NewService(
                (SiteSettingKeys.AirQualityAlertThresholdAqi, "151"),
                (SiteSettingKeys.HazardMaxSeverityAgeHours, "two days"))
            .GetAsync(CancellationToken.None);

        Assert.Equal(151, snapshot.AirQualityAlertThresholdAqi);
        Assert.Equal(48, snapshot.HazardMaxSeverityAgeHours);
    }

    [Fact]
    public async Task ImmichBaseUrl_LosesItsTrailingSlash()
    {
        var snapshot = await NewService((SiteSettingKeys.ImmichBaseUrl, "https://photos.example.com/"))
            .GetAsync(CancellationToken.None);

        Assert.Equal("https://photos.example.com", snapshot.ImmichBaseUrl);
    }

    [Fact]
    public async Task ImmichApiKey_ComesBackReadable()
    {
        var snapshot = await NewService((SiteSettingKeys.ImmichApiKey, SecretProtector.Protect("immich-api-key")))
            .GetAsync(CancellationToken.None);

        // Deobfuscated here because the Photos module hands it to Immich as a
        // header, not to a screen - the same trade the Google and Anthropic
        // secrets above make.
        Assert.Equal("immich-api-key", snapshot.ImmichApiKey);
    }

    [Fact]
    public async Task ImmichSettings_AreNullWhenUnset()
    {
        var snapshot = await NewService().GetAsync(CancellationToken.None);

        Assert.Null(snapshot.ImmichBaseUrl);
        Assert.Null(snapshot.ImmichApiKey);
    }

    /// <summary>The cache doing its job: a second read inside the TTL does not go back to the table.</summary>
    [Fact]
    public async Task ASecondReadInsideTheTtlIsCached()
    {
        var (service, factory) = NewServiceWithStore((SiteSettingKeys.ImmichBaseUrl, "https://old.example.com"));

        await service.GetAsync(CancellationToken.None);
        await StoreAsync(factory, SiteSettingKeys.ImmichBaseUrl, "https://new.example.com");

        Assert.Equal("https://old.example.com", (await service.GetAsync(CancellationToken.None)).ImmichBaseUrl);
    }

    /// <summary>
    /// And the write-side signal that makes the TTL survivable. Without it,
    /// saving a setting and immediately reading back something derived from it -
    /// the admin Photos page's connection check - answers with the old value for
    /// up to thirty seconds, which reads as the save not having worked.
    /// </summary>
    [Fact]
    public async Task InvalidateSendsTheNextReadBackToTheTable()
    {
        var (service, factory) = NewServiceWithStore((SiteSettingKeys.ImmichBaseUrl, "https://old.example.com"));

        await service.GetAsync(CancellationToken.None);
        await StoreAsync(factory, SiteSettingKeys.ImmichBaseUrl, "https://new.example.com");
        service.Invalidate();

        Assert.Equal("https://new.example.com", (await service.GetAsync(CancellationToken.None)).ImmichBaseUrl);
    }

    /// <summary>NewService's factory, kept, for the two tests that write to the table after the service has read it.</summary>
    private static (SiteSettingsService Service, TestDbContextFactory Factory) NewServiceWithStore(
        params (string Key, string Value)[] settings)
    {
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        using (var db = factory.CreateDbContext())
        {
            db.SiteSettings.AddRange(settings.Select(s => new EfSiteSetting { Key = s.Key, Value = s.Value }));
            db.SaveChanges();
        }

        return (new SiteSettingsService(factory, new FakeTimeProvider()), factory);
    }

    private static async Task StoreAsync(TestDbContextFactory factory, string key, string value)
    {
        await using var db = await factory.CreateDbContextAsync();
        var setting = await db.SiteSettings.FirstAsync(s => s.Key == key);
        setting.Value = value;
        await db.SaveChangesAsync();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
        public Task<AerieContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AerieContext(options));
    }
}
