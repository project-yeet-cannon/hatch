using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Controllers;

/// <summary>Outdoor weather + sun for the house location.</summary>
[ApiController]
[Route("api/[controller]")]
public class OutsideController(IWeatherService weather) : ControllerBase
{
    [HttpGet]
    public Task<OutsideClimate> Get(
        [FromQuery] double? historyHours,
        [FromQuery] double? forecastHours,
        [FromQuery] double? bucketMinutes,
        CancellationToken ct)
        => weather.GetOutsideAsync(DashboardController.BuildWindow(historyHours, forecastHours, bucketMinutes), ct);
}
