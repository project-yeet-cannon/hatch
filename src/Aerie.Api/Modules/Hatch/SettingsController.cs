using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Services.Auth;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The two settings a Hatch install of its own configures: the Claude
/// subscription token that drives the battery, and what to call whoever is
/// sitting at this machine.
///
/// Here rather than on the admin app's Settings page because an install with no
/// admin app - which is every install that is only somebody's tracker - still
/// has to be able to set them. They are the same site settings the admin page
/// wrote, under the same keys, stored the same way, so an operator who set the
/// token before this existed finds it already set here.
///
/// Plain <c>[RequireAdmin]</c> with no <c>AcceptScope</c>, unlike
/// <see cref="UtilizationController"/> and <see cref="LocalPersonController"/>
/// beside it: those are reads an agent has a use for, and these are not. An
/// API key that could write here could set the name every event in the house is
/// signed with, or swap the credential the account's headroom is read through.
/// A person, at a browser, or nobody.
/// </summary>
[ApiController]
[Route("api/hatch/settings")]
[RequireAdmin]
public class SettingsController(
    AerieContext db,
    ISiteSettingsService siteSettings,
    IOptions<AuthOptions> authOptions) : ControllerBase
{
    /// <summary>What is stood in for a secret that is set. The admin app's SettingsController redacts to the same string, because it is the same answer.</summary>
    private const string Redacted = "••••••••";

    [HttpGet]
    public async Task<HatchSettingsDto> GetHatchSettings(CancellationToken ct) => await ReadAsync(ct);

    /// <summary>
    /// The bulk-edit convention, which is Hatch's convention everywhere
    /// (docs/hatch-planning.md): a field left out is left alone, <c>""</c>
    /// clears it, anything else replaces it.
    ///
    /// That is what makes "clearing the token removes it" a value rather than a
    /// second verb - <see cref="SecretProtector.Protect"/> answers <c>""</c>
    /// for an empty input and stores it untagged, so a cleared token reads back
    /// exactly like one nobody ever set and <see cref="IClaudeCredential"/>
    /// answers null for both.
    /// </summary>
    [HttpPut]
    public async Task<HatchSettingsDto> PutHatchSettings(HatchSettingsWriteRequest request, CancellationToken ct)
    {
        var wrote = false;

        if (request.ClaudeSubscriptionToken is { } token)
        {
            await UpsertAsync(SiteSettingKeys.ClaudeSubscriptionToken, SecretProtector.Protect(token), ct);
            wrote = true;
        }

        // Stored as it was typed. The read side already decides what an
        // unusable name means - LocalCaller.PersonNameOf falls back to config
        // and then to its own default for anything PersonName refuses - so a
        // name this page stores degrades exactly the way one from
        // Auth__LocalPerson__Name would, and guarding it twice would mean two
        // places to keep in step.
        if (request.LocalPersonName is { } name)
        {
            await UpsertAsync(SiteSettingKeys.LocalPersonName, name, ct);
            wrote = true;
        }

        // Before the read below, for the reason the admin controller gives:
        // somebody who saves a setting and is immediately shown something
        // derived from it should not be told the old answer for the length of
        // the cache TTL. It is also what makes the battery reflect a new token
        // without a restart.
        if (wrote) siteSettings.Invalidate();

        return await ReadAsync(ct);
    }

    private async Task UpsertAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.SiteSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.SiteSettings.Add(new EfSiteSetting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<HatchSettingsDto> ReadAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);

        return new HatchSettingsDto(
            // The snapshot has already deobfuscated the token, so "is it set"
            // is asked of the plaintext rather than of SecretProtector.HasValue,
            // which expects the stored form. Same answer, one query fewer - and
            // the value itself never leaves this method.
            ClaudeSubscriptionToken: string.IsNullOrWhiteSpace(settings.ClaudeSubscriptionToken) ? "" : Redacted,
            LocalPersonNameApplies: !authOptions.Value.Enabled,
            LocalPersonName: settings.LocalPersonName ?? "");
    }
}

/// <summary>
/// What Hatch's Settings page draws.
/// </summary>
/// <param name="ClaudeSubscriptionToken">Dots when one is set, empty when none is. Never the token: a page that held one would be a page that leaked one.</param>
/// <param name="LocalPersonNameApplies">
/// Whether there is a name here to set at all - false wherever the wall is up,
/// which is every cluster install, because there a person is a person because
/// they enrolled and their name is already on everything they write. The same
/// flag <see cref="LocalCaller"/> is gated on, so the page and the identity
/// agree by construction.
/// </param>
/// <param name="LocalPersonName">The name as stored, or empty. Not a secret, so it is handed back to be edited.</param>
public record HatchSettingsDto(string ClaudeSubscriptionToken, bool LocalPersonNameApplies, string LocalPersonName);

/// <summary>
/// An edit to either setting, or both. Null - or omitted - leaves one alone;
/// <c>""</c> clears it.
/// </summary>
public record HatchSettingsWriteRequest(string? ClaudeSubscriptionToken, string? LocalPersonName);
