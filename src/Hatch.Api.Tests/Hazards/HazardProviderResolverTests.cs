using Hatch.Api.Ef;
using Hatch.Api.Services.Hazards;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hatch.Api.Tests.Hazards;

/// <summary>
/// Covers the seam that keeps a US-only alert provider from being a decision
/// baked into the app (docs/kiosk-architecture.md): a name nothing
/// answers to disables that half of the feature instead of throwing.
/// </summary>
public class HazardProviderResolverTests
{
    [Fact]
    public void ResolvesTheProviderNamedBySetting()
    {
        var nws = new StubWeatherAlertProvider(HazardProviders.Nws);
        var resolver = NewResolver(weather: [new StubWeatherAlertProvider("other"), nws]);

        Assert.Same(nws, resolver.ResolveWeatherAlerts(HazardProviders.Nws));
    }

    [Theory]
    [InlineData("NWS")]
    [InlineData("  nws  ")]
    public void MatchesCaseInsensitivelyAndIgnoresSurroundingSpace(string configured)
    {
        // The value is typed into a text box on the settings page, and "NWS"
        // is how a person writes it.
        var nws = new StubWeatherAlertProvider(HazardProviders.Nws);
        var resolver = NewResolver(weather: [nws]);

        Assert.Same(nws, resolver.ResolveWeatherAlerts(configured));
    }

    [Fact]
    public void ResolvesEachHalfIndependently()
    {
        var openMeteo = new StubAirQualityProvider(HazardProviders.OpenMeteo);
        var resolver = NewResolver(
            weather: [new StubWeatherAlertProvider(HazardProviders.Nws)],
            airQuality: [openMeteo]);

        Assert.Same(openMeteo, resolver.ResolveAirQuality(HazardProviders.OpenMeteo));
        Assert.Null(resolver.ResolveAirQuality(HazardProviders.None));
        Assert.NotNull(resolver.ResolveWeatherAlerts(HazardProviders.Nws));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNull_WhenTheFeatureIsSwitchedOff(string configured)
    {
        var resolver = NewResolver(weather: [new StubWeatherAlertProvider(HazardProviders.Nws)]);

        Assert.Null(resolver.ResolveWeatherAlerts(configured));
    }

    [Fact]
    public void ReturnsNull_WhenNothingAnswersToTheName()
    {
        // A typo disables the feature. It must not throw: these settings are
        // free text, and the API has to boot and keep running regardless.
        var resolver = NewResolver(weather: [new StubWeatherAlertProvider(HazardProviders.Nws)]);

        Assert.Null(resolver.ResolveWeatherAlerts("nwss"));
    }

    [Fact]
    public void ReturnsNull_WhenNoProvidersAreRegisteredAtAll()
    {
        // The state the app ships in until B2/B3 land.
        var resolver = NewResolver();

        Assert.Null(resolver.ResolveWeatherAlerts(HazardProviders.Nws));
        Assert.Null(resolver.ResolveAirQuality(HazardProviders.OpenMeteo));
    }

    [Fact]
    public void PicksUpACorrectedSetting_WithoutARestart()
    {
        // Resolution happens per call rather than once at startup, so fixing
        // the typo takes effect on the next sync - the way every other
        // SiteSetting behaves.
        var nws = new StubWeatherAlertProvider(HazardProviders.Nws);
        var resolver = NewResolver(weather: [nws]);

        Assert.Null(resolver.ResolveWeatherAlerts("nwss"));
        Assert.Same(nws, resolver.ResolveWeatherAlerts(HazardProviders.Nws));
    }

    private static HazardProviderResolver NewResolver(
        IWeatherAlertProvider[]? weather = null,
        IAirQualityProvider[]? airQuality = null) =>
        new(weather ?? [], airQuality ?? [], NullLogger<HazardProviderResolver>.Instance);

    private sealed class StubWeatherAlertProvider(string name) : IWeatherAlertProvider
    {
        public string Name => name;

        public Task<IReadOnlyList<WeatherAlertRecord>> GetActiveAlertsAsync(
            double latitude, double longitude, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WeatherAlertRecord>>([]);
    }

    private sealed class StubAirQualityProvider(string name) : IAirQualityProvider
    {
        public string Name => name;

        public Task<AirQualityReading?> GetCurrentAsync(
            double latitude, double longitude, CancellationToken ct) =>
            Task.FromResult<AirQualityReading?>(null);
    }
}
