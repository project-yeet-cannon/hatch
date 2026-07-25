using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Services.Dashboard;

public interface IDashboardService
{
    Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// The backend-for-frontend aggregate: composes zones + outside into the exact
/// shape the dashboard app consumes. Zones, outside, and settings are fetched
/// concurrently so the (slower) weather call doesn't serialize behind the DB
/// reads.
/// </summary>
public class DashboardService(
    IZoneService zones,
    IWeatherService weather,
    ISiteSettingsService siteSettings,
    TimeProvider time) : IDashboardService
{
    public async Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct)
    {
        var zonesTask = zones.GetZonesAsync(window, ct);
        var outsideTask = weather.GetOutsideAsync(window, ct);
        var settingsTask = siteSettings.GetAsync(ct);
        await Task.WhenAll(zonesTask, outsideTask, settingsTask);

        var settings = await settingsTask;
        var now = time.GetUtcNow();
        return new DashboardData(
            GeneratedAt: now,
            Timezone: settings.TimeZone,
            Zones: await zonesTask,
            Outside: await outsideTask,
            SunEvents: SolarCalculator.EventsForDay(now, settings.Latitude, settings.Longitude));
    }
}
