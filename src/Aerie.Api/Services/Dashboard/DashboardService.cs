using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.Calendar;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Routines;

namespace Aerie.Api.Services.Dashboard;

public interface IDashboardService
{
    Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// The backend-for-frontend aggregate: composes zones + outside + routines +
/// calendar into the exact shape the dashboard app consumes. Zones, outside,
/// settings, routines, and the agenda are fetched concurrently so the (slower)
/// weather call doesn't serialize behind the DB reads.
/// </summary>
public class DashboardService(
    IZoneService zones,
    IWeatherService weather,
    ISiteSettingsService siteSettings,
    IRoutineService routines,
    ICalendarAgendaService calendar,
    TimeProvider time) : IDashboardService
{
    public async Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct)
    {
        var zonesTask = zones.GetZonesAsync(window, ct);
        var outsideTask = weather.GetOutsideAsync(window, ct);
        var settingsTask = siteSettings.GetAsync(ct);
        var routinesTask = routines.GetRoutinesAsync(ct);
        var calendarTask = calendar.GetAgendaAsync(ct);
        await Task.WhenAll(zonesTask, outsideTask, settingsTask, routinesTask, calendarTask);

        var settings = await settingsTask;
        var now = time.GetUtcNow();
        return new DashboardData(
            GeneratedAt: now,
            Timezone: settings.TimeZone,
            Zones: await zonesTask,
            Outside: await outsideTask,
            SunEvents: SolarCalculator.EventsForDay(now, settings.Latitude, settings.Longitude),
            Routines: await routinesTask,
            Calendar: await calendarTask);
    }
}
