using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>Indoor climate zones - the resource endpoints behind the dashboard aggregate.</summary>
[ApiController]
[Route("api/[controller]")]
public class ZonesController(IZoneService zones, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ZoneClimate>> GetAll(
        [FromQuery] double? historyHours,
        [FromQuery] double? forecastHours,
        [FromQuery] double? bucketMinutes,
        CancellationToken ct)
        => zones.GetZonesAsync(DashboardController.BuildWindow(historyHours, forecastHours, bucketMinutes), ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<ZoneClimate>> Get(
        string id,
        [FromQuery] double? historyHours,
        [FromQuery] double? forecastHours,
        [FromQuery] double? bucketMinutes,
        CancellationToken ct)
    {
        var zone = await zones.GetZoneAsync(id, DashboardController.BuildWindow(historyHours, forecastHours, bucketMinutes), ct);
        return zone is null ? NotFound() : zone;
    }

    /// <summary>Raw/bucketed temperature series for one zone over an explicit window.</summary>
    [HttpGet("{id}/readings")]
    public Task<IReadOnlyList<TempPoint>> GetReadings(
        string id,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] double? bucketMinutes,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var toValue = to ?? now;
        var fromValue = from ?? toValue - DashboardWindow.Default.History;
        var bucket = bucketMinutes is > 0 ? TimeSpan.FromMinutes(bucketMinutes.Value) : DashboardWindow.Default.Bucket;
        return zones.GetReadingsAsync(id, fromValue, toValue, bucket, ct);
    }

    [HttpGet("{id}/comfort")]
    public async Task<ActionResult<ComfortRange>> GetComfort(string id, CancellationToken ct)
    {
        var comfort = await zones.GetComfortAsync(id, ct);
        return comfort is null ? NotFound() : comfort;
    }

    [HttpPut("{id}/comfort")]
    public async Task<ActionResult<ComfortRange>> PutComfort(string id, [FromBody] ComfortRange range, CancellationToken ct)
    {
        if (range.HighF < range.LowF) return BadRequest("highF must be >= lowF");
        await zones.UpsertComfortAsync(id, range, ct);
        return range;
    }
}
