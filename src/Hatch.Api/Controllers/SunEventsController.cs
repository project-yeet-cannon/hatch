using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.Dashboard;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Controllers;

/// <summary>
/// Sun events (civil dawn, sunrise, sunset, civil dusk) for an arbitrary instant
/// and location. `at`/`lat`/`lon` default to now and the site's configured
/// location - the dashboard's own theming gets its events inline on
/// DashboardData instead of calling this, but the dev-only theme preview page
/// (apps/dashboard/dev-theme.html) hits this directly with overrides to scrub
/// through simulated dates/locations without duplicating the NOAA math in TS.
/// </summary>
[ApiController]
[Route("api/sun-events")]
public class SunEventsController(ISiteSettingsService siteSettings, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<SunEvents> Get(
        [FromQuery] DateTimeOffset? at,
        [FromQuery] double? lat,
        [FromQuery] double? lon,
        CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        return SolarCalculator.EventsForDay(
            at ?? time.GetUtcNow(),
            lat ?? settings.Latitude,
            lon ?? settings.Longitude);
    }
}
