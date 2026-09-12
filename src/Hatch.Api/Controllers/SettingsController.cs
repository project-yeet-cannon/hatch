using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Jobs;
using Hatch.Api.Models.DeviceMapping;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Controllers;

/// <summary>
/// CRUD over SiteSetting - the admin-editable scalars that replace the
/// "Dashboard" appsettings section (docs/device-architecture.md Phase 2/6).
///
/// Guarded whole, reads included, which makes it one of two exceptions to
/// "mutations only" (the other is AuthController's grant list). The reads here
/// are the connection settings for everything the house talks to, and while
/// Redact keeps the Home Assistant token itself out of the response, the host,
/// the port and the shape of the install are exactly the reconnaissance a
/// household member has no use for. Nothing outside the admin app has ever
/// called it.
/// </summary>
[ApiController]
[RequireAdmin]
[Route("api/[controller]")]
public class SettingsController(
    AppDbContext db,
    IHomeAssistantConnectionManager haConnection,
    ISiteSettingsService siteSettings,
    JobsInit jobs) : ControllerBase
{
    /// <summary>
    /// The three keys that are, between them, the Home Assistant connection -
    /// and therefore whether this installation has a house at all. Saving or
    /// clearing any of them re-applies the connection and re-decides the house
    /// jobs' schedule (see JobsInit.WireUpJobs), so an operator who connects a
    /// house gets its jobs without a restart and one who disconnects it stops
    /// them the same way. They are saved one key at a time, so a full
    /// connection only resolves after the third write and WireUpJobs runs three
    /// times; it is idempotent, which is what makes that fine.
    /// </summary>
    private static bool IsHomeAssistantConnection(string key) =>
        key is SiteSettingKeys.HomeAssistantHost or SiteSettingKeys.HomeAssistantPort
            or SiteSettingKeys.HomeAssistantToken;

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

        if (IsHomeAssistantConnection(key))
        {
            await haConnection.ApplyAsync(ct);

            // Better than the ApplyAsync above it, and for a reason worth
            // knowing: the Quartz store is shared, so this write reaches every
            // replica's schedule, where ClientFactory.Initialize reaches only
            // the one that took the request.
            await jobs.WireUpJobs();
        }

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

        // The other side of Upsert's branch. Without it, clearing a connection
        // would leave the house jobs scheduled against a house that is no
        // longer there.
        if (IsHomeAssistantConnection(key))
            await jobs.WireUpJobs();

        return NoContent();
    }

    /// <summary>The settings stored obfuscated rather than in cleartext. Adding a key here is the whole job - it drives both the write-side obfuscation and the read-side redaction.</summary>
    private static bool IsSecret(string key) =>
        key is SiteSettingKeys.HomeAssistantToken or SiteSettingKeys.KioskWifiPassword
            or SiteSettingKeys.GoogleClientSecret or SiteSettingKeys.AnthropicApiKey
            or SiteSettingKeys.ImmichApiKey or SiteSettingKeys.ClaudeSubscriptionToken;

    /// <summary>Secrets are stored protected, not encrypted (SecretProtector), so they're still redacted before leaving the API - no reason to hand back something trivially reversible.</summary>
    private static string Redact(string key, string value) =>
        IsSecret(key) && SecretProtector.HasValue(value) ? "••••••••" : value;
}
