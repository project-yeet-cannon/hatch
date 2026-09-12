using System.Collections.Concurrent;
using Hatch.Api.Ef;

namespace Hatch.Api.Services.Hazards;

/// <summary>
/// What both hazard provider interfaces share: a slug that a site setting can
/// name. It exists so the resolver is one method rather than two identical
/// ones, and so "provider" means the same thing on both halves of the feature.
/// </summary>
public interface IHazardProvider
{
    /// <summary>The value the corresponding site setting is compared against, e.g. "nws" - see HazardProviders.</summary>
    string Name { get; }
}

/// <summary>
/// Turns the operator-typed provider settings into the registered provider
/// they name, or into null.
/// </summary>
public interface IHazardProviderResolver
{
    /// <summary>The provider named by the WeatherAlertProvider setting, or null when it is "none" or unrecognized.</summary>
    IWeatherAlertProvider? ResolveWeatherAlerts(string configuredName);

    /// <summary>The provider named by the AirQualityProvider setting, or null when it is "none" or unrecognized.</summary>
    IAirQualityProvider? ResolveAirQuality(string configuredName);
}

/// <summary>
/// Resolves providers by name at use time rather than binding one at startup.
///
/// Two consequences, both deliberate. A name nothing answers to disables that
/// half of the feature and logs a warning instead of throwing: these settings
/// are free text an admin types, and a typo in the air quality provider must
/// not stop the API from booting. And because the lookup happens per call,
/// correcting that typo takes effect on the next sync - no restart, matching
/// how every other SiteSetting behaves.
/// </summary>
public class HazardProviderResolver(
    IEnumerable<IWeatherAlertProvider> weatherAlertProviders,
    IEnumerable<IAirQualityProvider> airQualityProviders,
    ILogger<HazardProviderResolver> logger) : IHazardProviderResolver
{
    // Resolution runs every sync, so an unrecognized name would otherwise warn
    // on a 15-minute timer forever. Keyed by what was actually asked for, so
    // fixing the setting and then breaking it again still says so.
    private readonly ConcurrentDictionary<string, byte> warned = new();

    public IWeatherAlertProvider? ResolveWeatherAlerts(string configuredName) =>
        Resolve(configuredName, weatherAlertProviders, SiteSettingKeys.WeatherAlertProvider);

    public IAirQualityProvider? ResolveAirQuality(string configuredName) =>
        Resolve(configuredName, airQualityProviders, SiteSettingKeys.AirQualityProvider);

    private T? Resolve<T>(string configuredName, IEnumerable<T> providers, string settingKey)
        where T : class, IHazardProvider
    {
        var name = configuredName.Trim();

        // Case-insensitive because the value is typed into a text box, and
        // "NWS" is how a person writes it.
        if (name.Length == 0 || name.Equals(HazardProviders.None, StringComparison.OrdinalIgnoreCase)) return null;

        var provider = providers.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (provider is not null) return provider;

        if (warned.TryAdd($"{settingKey}:{name}", 0))
        {
            logger.LogWarning(
                "No provider named {ConfiguredName} is registered for {SettingKey}; that half of outdoor hazards is disabled. Registered: {Registered}",
                name, settingKey, string.Join(", ", providers.Select(p => p.Name)));
        }

        return null;
    }
}
