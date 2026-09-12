using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.Calendar;
using Hatch.Api.Services.DeviceMapping;
using Hatch.Api.Services.Hazards;
using Hatch.Api.Services.Panels;
using Hatch.Api.Services.Routines;

namespace Hatch.Api.Services.Dashboard;

public interface IDashboardService
{
    Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// The backend-for-frontend aggregate: composes zones + outside + routines +
/// cameras + panels + calendar + outdoor hazards into the exact shape the
/// dashboard app consumes.
/// Every part is fetched concurrently so the (slower) weather call doesn't
/// serialize behind the DB reads.
/// </summary>
public class DashboardService(
    IZoneService zones,
    IWeatherService weather,
    ISiteSettingsService siteSettings,
    IRoutineService routines,
    ICameraDirectory cameras,
    IPanelService panels,
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
        // Tiles only - an open panel's live state is its own endpoint, see PanelService.
        var panelsTask = panels.GetPanelsAsync(ct);
        var calendarTask = calendar.GetAgendaAsync(ct);
        var alertsTask = hazards.GetAlertsAsync(ct);
        await Task.WhenAll(zonesTask, outsideTask, settingsTask, routinesTask, camerasTask, panelsTask, calendarTask, alertsTask);

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
            Panels: await panelsTask,
            Calendar: await calendarTask,
            Alerts: await alertsTask);
    }
}
