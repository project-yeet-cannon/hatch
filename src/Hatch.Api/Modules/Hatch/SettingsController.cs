using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Models.Auth;
using Hatch.Api.Services.Auth;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hatch.Api.Modules.Hatch;

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
/// Both of those routes carry plain <c>[RequireAdmin]</c> with no
/// <c>AcceptScope</c>, unlike <see cref="UtilizationController"/> and
/// <see cref="LocalPersonController"/> beside them: those are reads an agent has
/// a use for, and these are not. An API key that could write here could set the
/// name every event in the house is signed with, or swap the credential the
/// account's headroom is read through. A person, at a browser, or nobody.
///
/// No class-level attribute even so, and that is the third time this split has
/// been drawn (<see cref="RunnersController"/>, <see cref="AssigneeController"/>,
/// <see cref="IssuePlaybookController"/>): <see cref="GetClaudeToken"/> below is
/// cut the other way - a keyless runner is its ordinary caller - and
/// <c>RequireAdminAttribute</c> is <c>AllowMultiple = false</c>, so a
/// method-level attribute silently *replaces* a class-level one rather than
/// tightening it. Decorating every action explicitly is what keeps the two
/// person-only routes person-only whatever is added beside them.
/// </summary>
[ApiController]
[Route("api/hatch/settings")]
public class SettingsController(
    AppDbContext db,
    ISiteSettingsService siteSettings,
    IClaudeCredential claudeCredential,
    IOptions<AuthOptions> authOptions) : ControllerBase
{
    /// <summary>What is stood in for a secret that is set. The admin app's SettingsController redacts to the same string, because it is the same answer.</summary>
    private const string Redacted = "••••••••";

    [HttpGet]
    [RequireAdmin]
    public async Task<HatchSettingsDto> GetHatchSettings(CancellationToken ct) => await ReadAsync(ct);

    /// <summary>
    /// The token itself, wrapped, for the one caller that has to have it: the
    /// container runner's entrypoint (containers/hatch-runner/entrypoint.sh),
    /// which starts a <c>claude</c> CLI of its own and has nowhere else to read
    /// a credential from. So a friend configures theirs on the Settings page and
    /// in no second place.
    /// </summary>
    /// <remarks>
    /// <para>Two gates, and they are independent on purpose. The attribute
    /// accepts a Hatch-scoped key or a keyless runner, unlike the two routes
    /// above it - a dispatcher is the ordinary caller here, and a person at a
    /// browser is the unusual one. Then the first line refuses everybody,
    /// caller and credential alike, wherever the wall is up: a token that
    /// crosses a network Hatch does not own is a different question from a
    /// token handed to a container on the same laptop, and this route only
    /// answers the second one. An install with its wall on has enrolled
    /// devices, TLS and somewhere to put a secret properly, and should.</para>
    ///
    /// <para><c>AuthErrorDto</c> and a bare 403 rather than <c>Forbid()</c>,
    /// which expects an authentication scheme this app does not register - the
    /// same shape <c>RequireAdminAttribute</c> itself refuses with, so both
    /// gates read alike from outside.</para>
    /// </remarks>
    [HttpGet("claude-token")]
    [RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
    public async Task<ActionResult<ClaudeTokenDto>> GetClaudeToken(CancellationToken ct)
    {
        if (authOptions.Value.Enabled)
        {
            return new ObjectResult(new AuthErrorDto(
                "this Hatch has its wall on, and a token that crosses a network is a different question - " +
                "the container runner is for an install with its wall off"))
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }

        // Through the credential rather than reading the setting again: it is
        // the one interface that produces this plaintext, and "not set" and
        // "set to blank" are already the same answer there.
        var token = await claudeCredential.GetTokenAsync(ct);
        if (token is null) return NoContent();

        return new ClaudeTokenDto(SecretProtector.Protect(token));
    }

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
    [RequireAdmin]
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
