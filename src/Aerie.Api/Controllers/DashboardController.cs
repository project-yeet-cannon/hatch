using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>
/// Primary dashboard endpoint. Returns the whole DashboardData snapshot the
/// frontend renders - this is what the dashboard app's ApiDashboardDataSource calls.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DashboardController(IDashboardService dashboard) : ControllerBase
{
    /// <param name="historyHours">Hours of history to include (default 9).</param>
    /// <param name="forecastHours">Hours of forecast to project (default 7).</param>
    /// <param name="bucketMinutes">Downsampling bucket width in minutes (default 30).</param>
    [HttpGet]
    public Task<DashboardData> Get(
        [FromQuery] double? historyHours,
        [FromQuery] double? forecastHours,
        [FromQuery] double? bucketMinutes,
        CancellationToken ct)
        => dashboard.GetDashboardAsync(BuildWindow(historyHours, forecastHours, bucketMinutes), ct);

    internal static DashboardWindow BuildWindow(double? historyHours, double? forecastHours, double? bucketMinutes)
    {
        var d = DashboardWindow.Default;
        return new DashboardWindow(
            historyHours is > 0 ? TimeSpan.FromHours(historyHours.Value) : d.History,
            forecastHours is >= 0 ? TimeSpan.FromHours(forecastHours.Value) : d.Forecast,
            bucketMinutes is > 0 ? TimeSpan.FromMinutes(bucketMinutes.Value) : d.Bucket);
    }
}
