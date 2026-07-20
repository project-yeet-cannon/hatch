using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using Aerie.Api.Models.DeviceMapping;
using Aerie.Api.Services.Dashboard;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Controllers;

/// <summary>
/// Zone CRUD for the Zone/Device/DeviceChannel domain (docs/device-architecture.md
/// Phase 2), plus the dashboard's climate read path, which still runs on the
/// pre-Phase-1 EfZoneConfig/EnvironmentReading tables until Phase 5 re-points it.
/// To keep both under one controller without route collisions, the climate
/// endpoints live under a "climate" sub-path (GET api/zones/climate, GET
/// api/zones/{id}/climate, keyed by HA entity id) while the bare routes are the
/// new admin CRUD, keyed by the Zone's Guid id.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ZonesController(IZoneService zones, AerieContext db, TimeProvider time) : ControllerBase
{
    // ---- Admin CRUD (EfZone) ----

    [HttpGet]
    public async Task<IReadOnlyList<ZoneDto>> GetAll(CancellationToken ct)
        => await db.Zones.AsNoTracking()
            .OrderBy(z => z.SortOrder)
            .Select(z => new ZoneDto(z.Id, z.Name, z.Kind, z.ComfortLowF, z.ComfortHighF, z.SortOrder, z.Included))
            .ToListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ZoneDto>> Get(Guid id, CancellationToken ct)
    {
        var zone = await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == id, ct);
        return zone is null ? NotFound() : ToDto(zone);
    }

    [HttpPost]
    public async Task<ActionResult<ZoneDto>> Create(ZoneWriteRequest request, CancellationToken ct)
    {
        var zone = new EfZone
        {
            Name = request.Name,
            Kind = request.Kind,
            ComfortLowF = request.ComfortLowF,
            ComfortHighF = request.ComfortHighF,
            SortOrder = request.SortOrder,
            Included = request.Included,
        };
        db.Zones.Add(zone);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = zone.Id }, ToDto(zone));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ZoneDto>> Update(Guid id, ZoneWriteRequest request, CancellationToken ct)
    {
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct);
        if (zone is null) return NotFound();

        zone.Name = request.Name;
        zone.Kind = request.Kind;
        zone.ComfortLowF = request.ComfortLowF;
        zone.ComfortHighF = request.ComfortHighF;
        zone.SortOrder = request.SortOrder;
        zone.Included = request.Included;
        await db.SaveChangesAsync(ct);
        return ToDto(zone);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var zone = await db.Zones.FindAsync([id], ct);
        if (zone is null) return NotFound();
        db.Zones.Remove(zone);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static ZoneDto ToDto(EfZone z) => new(z.Id, z.Name, z.Kind, z.ComfortLowF, z.ComfortHighF, z.SortOrder, z.Included);

    // ---- Dashboard climate read path (pre-Phase-5; see ZoneService) ----

    [HttpGet("climate")]
    public Task<IReadOnlyList<ZoneClimate>> GetAllClimate(
        [FromQuery] double? historyHours,
        [FromQuery] double? forecastHours,
        [FromQuery] double? bucketMinutes,
        CancellationToken ct)
        => zones.GetZonesAsync(DashboardController.BuildWindow(historyHours, forecastHours, bucketMinutes), ct);

    [HttpGet("{id}/climate")]
    public async Task<ActionResult<ZoneClimate>> GetClimate(
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
