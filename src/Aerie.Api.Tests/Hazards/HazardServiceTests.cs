using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.Hazards;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hazards;

/// <summary>
/// Covers the judgment the read path makes on the sync's behalf: what counts
/// as news (the AQI threshold), what has stopped being news (an expired or
/// deactivated alert), and what order the wall says it in. Every failure here
/// is silent on a working-looking dashboard - a warning that stayed up after
/// it ended, or bad air nobody was told about.
/// </summary>
public class HazardServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BelowThresholdAirQualityProducesNoAlert()
    {
        // 100 is the top of Moderate and one below the default threshold. Air
        // this clean is not news, and a number on the wall about it is clutter.
        var db = await Seed(samples: [Sample(Now.AddMinutes(-20), 100)]);

        Assert.Empty(await NewService(db).GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AtThresholdAirQualityProducesOneAlert()
    {
        var db = await Seed(samples: [Sample(Now.AddMinutes(-20), 101)]);

        var alert = Assert.Single(await NewService(db).GetAlertsAsync(CancellationToken.None));

        Assert.Equal(HazardKind.AirQuality, alert.Kind);
        Assert.Equal("Unhealthy for Sensitive Groups", alert.Title);
        Assert.Equal("US AQI 101", alert.Detail);
        Assert.Equal("Moderate", alert.Severity);
        // Nothing is coming that is worse, so there is no hour to point at.
        Assert.Null(alert.StartsAt);
    }

    [Fact]
    public async Task AFuturePeakIsDescribedAsFuture()
    {
        var db = await Seed(samples:
        [
            Sample(Now.AddMinutes(-20), 60),
            Sample(Now.AddHours(6), 143),
        ]);

        var alert = Assert.Single(await NewService(db).GetAlertsAsync(CancellationToken.None));

        // The current reading is well below the threshold; the evening is not,
        // and that gap is the entire reason the peak is stored.
        Assert.Equal("Unhealthy for Sensitive Groups", alert.Title);
        Assert.Equal("US AQI 60 now, rising to 143", alert.Detail);
        Assert.Equal(Now.AddHours(6), alert.StartsAt);
    }

    [Fact]
    public async Task APeakBeyondTheComingDayIsNotNews()
    {
        var db = await Seed(samples:
        [
            Sample(Now.AddMinutes(-20), 60),
            Sample(Now.AddHours(30), 190),
        ]);

        Assert.Empty(await NewService(db).GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StaleAirQualityDataSaysNothingRatherThanReassuring()
    {
        // Four hours old means the feed has been down for a while. Silence is
        // honest; a reading from this morning presented as "now" is not.
        var db = await Seed(samples: [Sample(Now.AddHours(-4), 180)]);

        Assert.Empty(await NewService(db).GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ThresholdIsOperatorConfigurable()
    {
        var db = await Seed(samples: [Sample(Now.AddMinutes(-20), 60)]);

        var alert = Assert.Single(await NewService(db, threshold: 51).GetAlertsAsync(CancellationToken.None));

        Assert.Equal("Moderate", alert.Title);
        // Even below the default threshold the band still maps to a severity,
        // so an operator who lowers the bar gets a quiet alert rather than an
        // unstyled one.
        Assert.Equal("Minor", alert.Severity);
    }

    [Fact]
    public async Task InactiveWeatherAlertsAreExcluded()
    {
        var db = await Seed(alerts: [Alert("blizzard", active: false)]);

        Assert.Empty(await NewService(db).GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredWeatherAlertsAreExcluded()
    {
        // Still Active because the job has not fired since it ended. Expiry is
        // re-checked on read so an alert ends on the minute it ends rather
        // than up to fifteen minutes later.
        var db = await Seed(alerts: [Alert("heat", ends: Now.AddMinutes(-1))]);

        Assert.Empty(await NewService(db).GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AlertsStartingBeyondTheWindowAreExcluded()
    {
        var db = await Seed(alerts: [Alert("thursday-storm", onset: Now.AddHours(60), ends: Now.AddHours(66))]);

        Assert.Empty(await NewService(db).GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AlertsFromAProviderNoLongerConfiguredAreExcluded()
    {
        // The sync job only ever deactivates rows belonging to the provider it
        // just fetched from, so without this filter an old provider's last
        // alerts would stay on the wall forever.
        var db = await Seed(alerts: [Alert("stale", source: "some-other-provider")]);

        Assert.Empty(await NewService(db).GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TurningAProviderOffEmptiesItsHalf()
    {
        var db = await Seed(
            alerts: [Alert("tornado")],
            samples: [Sample(Now.AddMinutes(-20), 180)]);

        var service = NewService(db, weatherProvider: HazardProviders.None, airQualityProvider: HazardProviders.None);

        Assert.Empty(await service.GetAlertsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MostSevereComesFirst()
    {
        var db = await Seed(
            alerts:
            [
                Alert("advisory", severity: WeatherAlertSeverity.Minor),
                Alert("tornado", severity: WeatherAlertSeverity.Extreme),
                Alert("winter-storm", severity: WeatherAlertSeverity.Severe),
            ],
            samples: [Sample(Now.AddMinutes(-20), 180)]);

        var alerts = await NewService(db).GetAlertsAsync(CancellationToken.None);

        Assert.Equal(
            ["tornado", "winter-storm", "advisory"],
            alerts.Where(a => a.Kind == HazardKind.Weather).Select(a => a.Title));
    }

    [Fact]
    public async Task AWeatherAlertCarriesItsHeadlineRatherThanItsNarrative()
    {
        var db = await Seed(alerts: [Alert("heat", headline: "Heat Advisory until 6 PM", description: "* WHAT...\n\n* WHEN...")]);

        var alert = Assert.Single(await NewService(db).GetAlertsAsync(CancellationToken.None));

        // Paragraphs written for a broadcast feed have no business on a wall
        // display; the one-sentence headline does.
        Assert.Equal("Heat Advisory until 6 PM", alert.Detail);
    }

    [Fact]
    public async Task AnAlertAlreadyInEffectSortsAheadOfOneStillToCome()
    {
        var db = await Seed(alerts:
        [
            Alert("later-today", onset: Now.AddHours(4)),
            Alert("in-effect", onset: null),
        ]);

        var alerts = await NewService(db).GetAlertsAsync(CancellationToken.None);

        Assert.Equal(["in-effect", "later-today"], alerts.Select(a => a.Title));
    }

    private static HazardService NewService(
        IDbContextFactory<AerieContext> db,
        int threshold = 101,
        string weatherProvider = HazardProviders.Nws,
        string airQualityProvider = HazardProviders.OpenMeteo) =>
        new(db,
            new StubSiteSettings(
                airQualityAlertThresholdAqi: threshold,
                weatherAlertProvider: weatherProvider,
                airQualityProvider: airQualityProvider),
            new FakeTimeProvider(Now));

    private static EfWeatherAlert Alert(
        string name,
        WeatherAlertSeverity severity = WeatherAlertSeverity.Severe,
        bool active = true,
        DateTimeOffset? onset = null,
        DateTimeOffset? ends = null,
        string? headline = null,
        string? description = null,
        string source = HazardProviders.Nws) => new()
        {
            Source = source,
            ProviderAlertId = name,
            Event = name,
            Headline = headline,
            Description = description,
            Severity = severity,
            Active = active,
            Onset = onset,
            Ends = ends ?? Now.AddHours(6),
            FetchedAt = Now,
        };

    private static EfAirQualitySample Sample(DateTimeOffset at, int usAqi) => new()
    {
        Source = HazardProviders.OpenMeteo,
        Timestamp = at,
        UsAqi = usAqi,
        FetchedAt = Now,
    };

    /// <summary>Rows staged directly, the way the sync job would have left them - this service never fetches.</summary>
    private static async Task<IDbContextFactory<AerieContext>> Seed(
        EfWeatherAlert[]? alerts = null, EfAirQualitySample[]? samples = null)
    {
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        await using var db = await factory.CreateDbContextAsync();
        db.WeatherAlerts.AddRange(alerts ?? []);
        db.AirQualitySamples.AddRange(samples ?? []);
        await db.SaveChangesAsync();
        return factory;
    }

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
        public Task<AerieContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AerieContext(options));
    }
}
