using Aerie.Api.Ef;
using Aerie.Api.Services.Hazards;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Hazards;

/// <summary>
/// Covers what the sync does to rows that already exist, which is where a
/// caching job earns or loses its keep: an alert that ended has to stop being
/// active, an alert that changed has to follow, and an hour already stored must
/// not be stored twice. A provider that throws must cost its own half and
/// nothing else.
/// </summary>
public class HazardSyncServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StoresAFetchedAlert()
    {
        var db = await NewDb();
        var result = await NewService(db, alerts: [Record("tornado")]).SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Rows(db));
        Assert.Equal("tornado", stored.ProviderAlertId);
        Assert.Equal(HazardProviders.Nws, stored.Source);
        Assert.True(stored.Active);
        Assert.Equal(Now, stored.FetchedAt);
        Assert.Equal(1, result.AlertsWritten);
    }

    [Fact]
    public async Task AnAlertTheProviderStopsReturningIsDeactivatedRatherThanDeleted()
    {
        var db = await NewDb(new EfWeatherAlert
        {
            Source = HazardProviders.Nws,
            ProviderAlertId = "last-night",
            Event = "Winter Storm Warning",
            Active = true,
            FetchedAt = Now.AddHours(-8),
        });

        var result = await NewService(db, alerts: []).SyncAsync(CancellationToken.None);

        // The row survives: "what was the house warned about last night" is
        // worth keeping, and these rows are tiny.
        var stored = Assert.Single(await Rows(db));
        Assert.False(stored.Active);
        Assert.Equal(1, result.AlertsDeactivated);
    }

    [Fact]
    public async Task AnAlertStillInEffectIsUpdatedInPlace()
    {
        var db = await NewDb(new EfWeatherAlert
        {
            Source = HazardProviders.Nws,
            ProviderAlertId = "heat",
            Event = "Heat Advisory",
            Severity = WeatherAlertSeverity.Minor,
            Ends = Now.AddHours(1),
            Active = true,
            FetchedAt = Now.AddMinutes(-15),
        });

        // NWS re-sends a live alert as an Update with a changed end time, which
        // the row has to follow rather than expiring on the old one.
        await NewService(db, alerts: [Record("heat", severity: WeatherAlertSeverity.Severe, ends: Now.AddHours(6))])
            .SyncAsync(CancellationToken.None);

        var stored = Assert.Single(await Rows(db));
        Assert.Equal(WeatherAlertSeverity.Severe, stored.Severity);
        Assert.Equal(Now.AddHours(6), stored.Ends);
    }

    [Fact]
    public async Task AnAlertThatLapsedAndCameBackIsReactivated()
    {
        var db = await NewDb(new EfWeatherAlert
        {
            Source = HazardProviders.Nws,
            ProviderAlertId = "flood",
            Event = "Flood Warning",
            Active = false,
            FetchedAt = Now.AddHours(-20),
        });

        await NewService(db, alerts: [Record("flood")]).SyncAsync(CancellationToken.None);

        Assert.True(Assert.Single(await Rows(db)).Active);
    }

    [Fact]
    public async Task StoresTheHourlySeriesWithTheCurrentHourCarryingItsComponents()
    {
        var db = await NewDb();

        // The current block is the only one carrying pollutants, and its
        // timestamp is a quarter-hour observation - so it folds onto its own
        // hour rather than becoming a 97th row a day that lines up with
        // nothing.
        var reading = new AirQualityReading(
            Current: new AirQualitySampleRecord(Now.AddMinutes(15), 118, Pm25: 38.4m, Pm10: 44.1m),
            Peak: new AirQualitySampleRecord(Now.AddHours(6), 143),
            Hourly:
            [
                new AirQualitySampleRecord(Now, 110),
                new AirQualitySampleRecord(Now.AddHours(6), 143),
            ]);

        var result = await NewService(db, reading: reading).SyncAsync(CancellationToken.None);

        var samples = await db.CreateDbContext().AirQualitySamples.OrderBy(s => s.Timestamp).ToListAsync();
        Assert.Equal(2, samples.Count);
        Assert.Equal(Now, samples[0].Timestamp);
        Assert.Equal(118, samples[0].UsAqi);
        Assert.Equal(38.4m, samples[0].Pm25);
        Assert.Equal(143, samples[1].UsAqi);
        Assert.Equal(2, result.SamplesWritten);
    }

    [Fact]
    public async Task AnHourAlreadyStoredIsNotStoredTwice()
    {
        var db = await NewDb();
        var reading = new AirQualityReading(
            Current: new AirQualitySampleRecord(Now, 110),
            Peak: new AirQualitySampleRecord(Now, 110),
            Hourly: [new AirQualitySampleRecord(Now, 110)]);

        await NewService(db, reading: reading).SyncAsync(CancellationToken.None);
        // The same hour, fetched again fifteen minutes later - which is most of
        // what a 15-minute job over hourly data does.
        var second = await NewService(db, reading: reading).SyncAsync(CancellationToken.None);

        Assert.Single(await db.CreateDbContext().AirQualitySamples.ToListAsync());
        Assert.Equal(0, second.SamplesWritten);
    }

    [Fact]
    public async Task AFailingWeatherProviderLeavesAirQualityAlone()
    {
        var db = await NewDb();
        var reading = new AirQualityReading(
            Current: new AirQualitySampleRecord(Now, 110),
            Peak: new AirQualitySampleRecord(Now, 110),
            Hourly: [new AirQualitySampleRecord(Now, 110)]);

        var result = await NewService(db, reading: reading, weatherThrows: true).SyncAsync(CancellationToken.None);

        Assert.True(result.WeatherFailed);
        Assert.False(result.AirQualityFailed);
        Assert.Equal(1, result.SamplesWritten);
    }

    [Fact]
    public async Task AProviderSetToNoneIsSkippedEntirely()
    {
        var db = await NewDb();

        var result = await NewService(
            db,
            alerts: [Record("tornado")],
            weatherProvider: HazardProviders.None,
            airQualityProvider: HazardProviders.None).SyncAsync(CancellationToken.None);

        Assert.Empty(await Rows(db));
        Assert.Equal(new HazardSyncResult(0, 0, 0, false, false), result);
    }

    private static WeatherAlertRecord Record(
        string id,
        WeatherAlertSeverity severity = WeatherAlertSeverity.Severe,
        DateTimeOffset? ends = null) =>
        new(id, "Winter Storm Warning", null, null, null, severity, null, ends ?? Now.AddHours(6), null);

    private static async Task<List<EfWeatherAlert>> Rows(IDbContextFactory<AerieContext> db) =>
        await db.CreateDbContext().WeatherAlerts.AsNoTracking().ToListAsync();

    private static HazardSyncService NewService(
        IDbContextFactory<AerieContext> db,
        IReadOnlyList<WeatherAlertRecord>? alerts = null,
        AirQualityReading? reading = null,
        bool weatherThrows = false,
        string weatherProvider = HazardProviders.Nws,
        string airQualityProvider = HazardProviders.OpenMeteo) =>
        new(db.CreateDbContext(),
            new StubHazardProviders(
                new StubWeatherAlertProvider(alerts ?? [], weatherThrows),
                new StubAirQualityProvider(reading)),
            new StubSiteSettings(weatherAlertProvider: weatherProvider, airQualityProvider: airQualityProvider),
            new FakeTimeProvider(Now),
            NullLogger<HazardSyncService>.Instance);

    private static async Task<IDbContextFactory<AerieContext>> NewDb(params EfWeatherAlert[] alerts)
    {
        var factory = new TestDbContextFactory(
            new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        await using var db = await factory.CreateDbContextAsync();
        db.WeatherAlerts.AddRange(alerts);
        await db.SaveChangesAsync();
        return factory;
    }

    /// <summary>Hands back whichever stub the setting names, so "none" and a real provider take the same path the resolver would.</summary>
    private sealed class StubHazardProviders(IWeatherAlertProvider weather, IAirQualityProvider air) : IHazardProviderResolver
    {
        public IWeatherAlertProvider? ResolveWeatherAlerts(string configuredName) =>
            configuredName == HazardProviders.None ? null : weather;

        public IAirQualityProvider? ResolveAirQuality(string configuredName) =>
            configuredName == HazardProviders.None ? null : air;
    }

    /// <summary>Throws on demand, which a real provider never does - the job's try/catch is insurance against a bug in one, and this is what tests it.</summary>
    private sealed class StubWeatherAlertProvider(IReadOnlyList<WeatherAlertRecord> alerts, bool throws) : IWeatherAlertProvider
    {
        public string Name => HazardProviders.Nws;

        public Task<IReadOnlyList<WeatherAlertRecord>> GetActiveAlertsAsync(double latitude, double longitude, CancellationToken ct) =>
            throws ? throw new HttpRequestException("upstream is down") : Task.FromResult(alerts);
    }

    private sealed class StubAirQualityProvider(AirQualityReading? reading) : IAirQualityProvider
    {
        public string Name => HazardProviders.OpenMeteo;

        public Task<AirQualityReading?> GetCurrentAsync(double latitude, double longitude, CancellationToken ct) =>
            Task.FromResult(reading);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
        public Task<AerieContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AerieContext(options));
    }
}
