using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Controllers;

/// <summary>CRUD over SiteSetting - the admin-editable scalars that replace the "Dashboard" appsettings section (docs/device-architecture.md Phase 2/6).</summary>
[ApiController]
[Route("api/[controller]")]
public class SettingsController(AerieContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<SiteSettingDto>> GetAll(CancellationToken ct)
        => await db.SiteSettings.AsNoTracking()
            .Select(s => new SiteSettingDto(s.Key, s.Value))
            .ToListAsync(ct);

    [HttpGet("{key}")]
    public async Task<ActionResult<SiteSettingDto>> Get(string key, CancellationToken ct)
    {
        var setting = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting is null ? NotFound() : new SiteSettingDto(setting.Key, setting.Value);
    }

    /// <summary>Creates or updates the setting at Key - the client controls the id, so upsert-by-PUT covers both create and update.</summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<SiteSettingDto>> Upsert(string key, SiteSettingWriteRequest request, CancellationToken ct)
    {
        var setting = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            setting = new EfSiteSetting { Key = key, Value = request.Value };
            db.SiteSettings.Add(setting);
        }
        else
        {
            setting.Value = request.Value;
        }
        await db.SaveChangesAsync(ct);
        return new SiteSettingDto(setting.Key, setting.Value);
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        var setting = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) return NotFound();
        db.SiteSettings.Remove(setting);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
