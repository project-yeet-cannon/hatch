using Hatch.Api.Models.AppVersion;
using Hatch.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Controllers;

/// <summary>
/// Lets a long-lived frontend notice that the build it's running has been
/// superseded. The kiosk dashboard is the reason this exists: the tablet shell
/// (apps/kiosk) loads the page once at boot and never navigates again, so
/// without a drift check a wall display keeps rendering whichever bundle was
/// deployed the last time the tablet rebooted. See
/// <see cref="IAppVersionService"/> for what the token is derived from.
/// </summary>
[ApiController]
[Route("api/app-version")]
public class AppVersionController(IAppVersionService versions) : ControllerBase
{
    /// <param name="app">Frontend app directory name under wwwroot/apps, e.g. "dashboard".</param>
    [HttpGet("{app}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AppVersionInfo> Get(string app)
    {
        var info = versions.GetVersion(app);
        if (info is null) return NotFound();

        // The whole point of the call is "what is deployed *right now*" - a
        // cached answer is a wrong answer, and this is polled from a device
        // whose HTTP cache we don't otherwise control.
        Response.Headers.CacheControl = "no-store";
        return info;
    }
}
