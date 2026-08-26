using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Controllers;

/// <summary>CRUD over SiteSetting - the admin-editable scalars that replace the "Dashboard" appsettings section (docs/device-architecture.md Phase 2/6).</summary>
[ApiController]
[Route("api/[controller]")]
public class SettingsController(
    AerieContext db,
    IHomeAssistantConnectionManager haConnection,
    ISiteSettingsService siteSettings) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<SiteSettingDto>> GetAll(CancellationToken ct)
    {
        var settings = await db.SiteSettings.AsNoTracking().ToListAsync(ct);
        return settings.Select(s => new SiteSettingDto(s.Key, Redact(s.Key, s.Value))).ToList();
    }

    [HttpGet("{key}")]
    public async Task<ActionResult<SiteSettingDto>> Get(string key, CancellationToken ct)
    {
        var setting = await db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting is null ? NotFound() : new SiteSettingDto(setting.Key, Redact(setting.Key, setting.Value));
    }

    /// <summary>Creates or updates the setting at Key - the client controls the id, so upsert-by-PUT covers both create and update.</summary>
    [HttpPut("{key}")]
    public async Task<ActionResult<SiteSettingDto>> Upsert(string key, SiteSettingWriteRequest request, CancellationToken ct)
    {
        var value = IsSecret(key)
            ? SecretProtector.Protect(request.Value)
            : request.Value;

        var setting = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            setting = new EfSiteSetting { Key = key, Value = value };
            db.SiteSettings.Add(setting);
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync(ct);

        // Before anything reads back: an admin who saves a setting and is
        // immediately shown something derived from it - the Photos page's
        // connection check is the sharp case - should not be told the old
        // answer for the length of the cache TTL.
        siteSettings.Invalidate();

        if (key is SiteSettingKeys.HomeAssistantHost or SiteSettingKeys.HomeAssistantPort or SiteSettingKeys.HomeAssistantToken)
            await haConnection.ApplyAsync(ct);

        return new SiteSettingDto(setting.Key, Redact(setting.Key, setting.Value));
    }

    [HttpDelete("{key}")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        var setting = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) return NotFound();
        db.SiteSettings.Remove(setting);
        await db.SaveChangesAsync(ct);
        siteSettings.Invalidate();
        return NoContent();
    }

    /// <summary>The settings stored obfuscated rather than in cleartext. Adding a key here is the whole job - it drives both the write-side obfuscation and the read-side redaction.</summary>
    private static bool IsSecret(string key) =>
        key is SiteSettingKeys.HomeAssistantToken or SiteSettingKeys.KioskWifiPassword
            or SiteSettingKeys.GoogleClientSecret or SiteSettingKeys.AnthropicApiKey
            or SiteSettingKeys.ImmichApiKey;

    /// <summary>Secrets are stored protected, not encrypted (SecretProtector), so they're still redacted before leaving the API - no reason to hand back something trivially reversible.</summary>
    private static string Redact(string key, string value) =>
        IsSecret(key) && SecretProtector.HasValue(value) ? "••••••••" : value;
}
