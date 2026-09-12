using System.Text;
using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Services.Calendar;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Controllers;

/// <summary>
/// The two ends of Google's authorization code flow. Unlike the rest of the
/// API these endpoints are navigated to, not fetched: the admin page links to
/// <c>start</c>, Google redirects the browser back to <c>callback</c>, and both
/// outcomes end on the admin Calendars page rather than in a JSON body.
///
/// Every route here sits behind the house wall (docs/auth-architecture.md), and
/// <c>start</c> behind the admin flag on top of it - connecting the household's
/// calendar account to Google is an operator's act. It is the one guarded
/// endpoint in the app that a browser *navigates* to rather than fetches, so a
/// refusal renders as a bare JSON body; that is tolerable because the only link
/// to it lives on a page the same flag already withholds.
/// </summary>
[ApiController]
[Route("api/calendar/oauth")]
public class CalendarOAuthController(
    AppDbContext db,
    ISiteSettingsService siteSettings,
    IGoogleOAuthService oauth,
    ICalendarDiscoveryService discovery,
    TimeProvider time,
    ILogger<CalendarOAuthController> logger) : ControllerBase
{
    /// <summary>Where both outcomes land the browser. The page reads <c>?connected</c> / <c>?error</c> off its own query string.</summary>
    private const string AdminCalendarsPath = "/apps/admin/calendars";

    /// <summary>How long an unfinished authorization stays spendable. Long enough for a first-time consent screen, short enough that an abandoned one is gone before anyone notices it.</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Starts the flow: records what the callback will need, then hands the browser to Google.</summary>
    [RequireAdmin]
    [HttpGet("start")]
    public async Task<IActionResult> Start(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        if (settings.GoogleClientId is not { } clientId || settings.GoogleClientSecret is null)
            return Problem(
                title: "Google OAuth is not configured",
                detail: "Set GoogleClientId and GoogleClientSecret on the admin Settings page, then connect the account again.",
                statusCode: StatusCodes.Status400BadRequest);

        var now = time.GetUtcNow();

        // Sweeping abandoned flows here rather than in a job: the rows are tiny
        // and rare, and starting a new flow is the one moment this table is
        // certain to be touched. A sweeper job would be more machinery than the
        // problem.
        await db.OAuthStates.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        var redirectUri = ResolveRedirectUri(settings);
        var state = Pkce.NewState();
        var verifier = Pkce.NewVerifier();

        db.OAuthStates.Add(new EfOAuthState
        {
            State = state,
            CodeVerifier = verifier,
            // Stored rather than re-derived, because the token exchange has to
            // repeat this exact string and the callback may land on a replica
            // that would derive it differently.
            RedirectUri = redirectUri,
            CreatedAt = now,
            ExpiresAt = now + StateLifetime,
        });
        await db.SaveChangesAsync(ct);

        return Redirect(oauth.BuildAuthorizationUrl(clientId, redirectUri, state, Pkce.ChallengeFor(verifier)));
    }

    /// <summary>
    /// Finishes the flow: spends the authorization code and stores the grant
    /// against the account Google says it belongs to.
    ///
    /// Unguarded, alone among the calendar endpoints, and deliberately. Nobody
    /// arrives here by choosing to: this is where Google sends the browser
    /// back, and what makes the request legitimate is the single-use state row
    /// spent below - a stronger claim than "an administrator is holding this
    /// tab", since only the browser that started the flow has the value. A
    /// guard here would add nothing and would turn a session that lapsed
    /// during a consent screen into a half-finished authorization with no way
    /// to retry.
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken ct)
    {
        // The admin declining the consent screen arrives here as access_denied.
        if (!string.IsNullOrWhiteSpace(error)) return BackToAdmin(error);
        if (string.IsNullOrWhiteSpace(state)) return BackToAdmin("missing_state");
        if (string.IsNullOrWhiteSpace(code)) return BackToAdmin("missing_code");

        var pending = await db.OAuthStates.AsNoTracking().FirstOrDefaultAsync(s => s.State == state, ct);
        if (pending is null) return BackToAdmin("unknown_state");

        // Single use, and single use across replicas: whoever's DELETE reports a
        // row is the one callback allowed to spend this authorization.
        if (await db.OAuthStates.Where(s => s.Id == pending.Id).ExecuteDeleteAsync(ct) == 0)
            return BackToAdmin("unknown_state");

        if (pending.ExpiresAt <= time.GetUtcNow()) return BackToAdmin("expired_state");

        var result = await oauth.ExchangeCodeAsync(code, pending.CodeVerifier, pending.RedirectUri, ct);
        if (!result.Succeeded) return BackToAdmin(result.Error ?? "token_exchange_failed");

        var tokens = result.Tokens;
        if (GoogleOAuthService.EmailFromIdToken(tokens.IdToken) is not { } email)
        {
            logger.LogWarning("Google returned tokens with no readable email in the id token");
            return BackToAdmin("no_account_email");
        }

        var account = await db.CalendarAccounts
            .FirstOrDefaultAsync(a => a.Provider == CalendarProviders.Google && a.AccountEmail == email, ct);

        // prompt=consent should make this unreachable. If it happens anyway,
        // storing the account would leave a row that can never refresh and can
        // only be fixed by deleting it - better to fail the connect outright.
        if (account is null && tokens.RefreshToken is null) return BackToAdmin("no_refresh_token");

        if (account is null)
        {
            account = new EfCalendarAccount
            {
                Provider = CalendarProviders.Google,
                AccountEmail = email,
                RefreshToken = SecretProtector.Protect(tokens.RefreshToken!),
                ConnectedAt = time.GetUtcNow(),
            };
            db.CalendarAccounts.Add(account);
        }
        else if (tokens.RefreshToken is { } reissued)
        {
            // Reconnecting the same account replaces the grant but keeps the
            // row, so the calendars hanging off it keep their Included and
            // ColorOverride settings.
            account.RefreshToken = SecretProtector.Protect(reissued);
        }

        account.AccessToken = SecretProtector.Protect(tokens.AccessToken);
        account.AccessTokenExpiresAt = tokens.ExpiresAt;
        account.NeedsReauth = false;
        account.LastSyncError = null;
        await db.SaveChangesAsync(ct);

        // Discover the account's calendars now, so it reaches the admin page
        // with a list to toggle rather than an empty panel and a button to
        // find. A failure here is recorded on the account and shown there -
        // it doesn't undo a connection that otherwise succeeded, and
        // "Refresh calendars" retries it.
        var discovered = await discovery.SyncCalendarListAsync(account.Id, ct);
        if (!discovered.Succeeded)
            logger.LogWarning(
                "Connected calendar account {AccountId} but could not list its calendars: {Error}",
                account.Id, discovered.Error);

        logger.LogInformation("Connected Google calendar account {AccountId}", account.Id);
        return Redirect(QueryHelpers.AddQueryString(AdminCalendarsPath, "connected", email));
    }

    /// <summary>
    /// Google compares the redirect URI byte-for-byte against what's registered
    /// in the operator's Cloud console, so the setting wins whenever it's set.
    /// Deriving it from the request is correct by default because Program.cs
    /// configures UseForwardedHeaders - Scheme and Host are the proxy's, not the
    /// container's - but a proxy that rewrites either is exactly why the
    /// override exists.
    /// </summary>
    private string ResolveRedirectUri(SiteSettingsSnapshot settings) =>
        settings.GoogleOAuthRedirectUri ?? $"{Request.Scheme}://{Request.Host}/api/calendar/oauth/callback";

    /// <summary>Ends a failed flow where the admin is looking, with a code the page can explain.</summary>
    private IActionResult BackToAdmin(string error)
    {
        var code = Sanitize(error);
        logger.LogWarning("Google OAuth callback failed: {Error}", code);
        return Redirect(QueryHelpers.AddQueryString(AdminCalendarsPath, "error", code));
    }

    /// <summary>
    /// Error codes reach this controller from the query string, so they are
    /// whatever the caller typed. Google's own are short snake_case ASCII;
    /// clamping to that shape keeps anything else from being reflected onward
    /// into the admin page's URL or a log line.
    /// </summary>
    private static string Sanitize(string error)
    {
        var clamped = new StringBuilder();
        foreach (var c in error.Trim().Take(64))
            clamped.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return clamped.Length == 0 ? "unknown_error" : clamped.ToString();
    }
}
