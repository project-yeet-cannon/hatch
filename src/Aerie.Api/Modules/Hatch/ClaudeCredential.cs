using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The Claude subscription token, or null.
///
/// One member, and null is an ordinary value rather than a startup failure:
/// an installation with no Claude subscription gets a Hatch with no battery,
/// no errors, and no empty box where a battery should be. Everything
/// downstream - <see cref="ClaudeUsageClient"/>, the cache, the endpoint -
/// binds to this rather than to where the token happens to be kept, so moving
/// it is a change to one class.
/// </summary>
public interface IClaudeCredential
{
    /// <summary>The token as currently configured, or null when there is none. Never throws, and never distinguishes "not set" from "set to blank".</summary>
    Task<string?> GetTokenAsync(CancellationToken ct);
}

/// <summary>
/// The token as a site setting, read fresh on each call.
///
/// Read rather than captured for the reason <see cref="Photos.ImmichClient"/>
/// re-reads its host and key: the setting is editable from the admin page
/// while the app runs, and an OAuth token that expires is pasted in again far
/// more often than the process restarts.
///
/// It is stored obfuscated and redacted on read like every other secret-valued
/// setting - see SettingsController's <c>IsSecret</c>, which is the whole of
/// that job - so the token reaches this class and nothing else.
/// </summary>
public class SiteSettingClaudeCredential(ISiteSettingsService siteSettings) : IClaudeCredential
{
    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var token = settings.ClaudeSubscriptionToken;
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}
