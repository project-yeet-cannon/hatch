using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.Calendar;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Hazards;
using Aerie.Api.Services.Routines;

namespace Aerie.Api.Services.Dashboard;

public interface IDashboardService
{
    Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// The backend-for-frontend aggregate: composes zones + outside + routines +
/// cameras + calendar + outdoor hazards into the exact shape the dashboard app
/// consumes.
/// Every part is fetched concurrently so the (slower) weather call doesn't
/// serialize behind the DB reads.
/// </summary>
public class DashboardService(
    IZoneService zones,
    IWeatherService weather,
    ISiteSettingsService siteSettings,
    IRoutineService routines,
    ICameraDirectory cameras,
    ICalendarAgendaService calendar,
    IHazardService hazards,
    TimeProvider time) : IDashboardService
{
    public async Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct)
    {
        var zonesTask = zones.GetZonesAsync(window, ct);
        var outsideTask = weather.GetOutsideAsync(window, ct);
        var settingsTask = siteSettings.GetAsync(ct);
        var routinesTask = routines.GetRoutinesAsync(ct);
        var camerasTask = cameras.GetCamerasAsync(ct);
        var calendarTask = calendar.GetAgendaAsync(ct);
        var alertsTask = hazards.GetAlertsAsync(ct);
        await Task.WhenAll(zonesTask, outsideTask, settingsTask, routinesTask, camerasTask, calendarTask, alertsTask);

        var settings = await settingsTask;
        var now = time.GetUtcNow();
        return new DashboardData(
            GeneratedAt: now,
            Timezone: settings.TimeZone,
            Zones: await zonesTask,
            Outside: await outsideTask,
            SunEvents: SolarCalculator.EventsForDay(now, settings.Latitude, settings.Longitude),
            Routines: await routinesTask,
            Cameras: await camerasTask,
            Calendar: await calendarTask,
            Alerts: await alertsTask);
    }
}
