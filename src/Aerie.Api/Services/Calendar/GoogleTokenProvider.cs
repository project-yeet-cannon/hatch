using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Calendar;

public interface IGoogleTokenProvider
{
    /// <summary>
    /// A usable access token for a connected account, refreshing first if the
    /// cached one is spent. Null means "don't call Google for this account
    /// right now" - it's unknown, disabled by a dead grant, or Google is
    /// unreachable. Callers log and skip; nothing here throws.
    /// </summary>
    Task<string?> GetAccessTokenAsync(Guid accountId, CancellationToken ct);
}

/// <summary>
/// The single place a Google access token is obtained, so every Calendar API
/// caller inherits the same refresh-ahead behaviour and the same handling of a
/// grant Google has stopped honouring.
///
/// Refreshes ahead of expiry rather than retrying on a 401: the sync job's
/// calls are batched and paginated, and discovering mid-pagination that the
/// token died would mean unwinding a partial fetch.
///
/// Two replicas can refresh the same account at once. That is safe rather than
/// coordinated: Google leaves previously-issued access tokens valid, so both
/// hold a working token and the later write simply wins the cache slot.
/// </summary>
public class GoogleTokenProvider(
    AerieContext db,
    IGoogleOAuthService oauth,
    TimeProvider time,
    ILogger<GoogleTokenProvider> logger) : IGoogleTokenProvider
{
    /// <summary>How close to expiry a cached token stops counting as usable. Covers the request it's about to be spent on plus clock skew against Google.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    public async Task<string?> GetAccessTokenAsync(Guid accountId, CancellationToken ct)
    {
        var account = await db.CalendarAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
        {
            logger.LogWarning("No calendar account {AccountId} to get a token for", accountId);
            return null;
        }

        // Asking again would only earn another invalid_grant. The account stays
        // in the list either way - the admin page offers "Reconnect", which is
        // what clears this.
        if (account.NeedsReauth) return null;

        var now = time.GetUtcNow();
        if (SecretObfuscator.TryReveal(account.AccessToken) is { } cached && account.AccessTokenExpiresAt > now + RefreshMargin)
            return cached;

        if (SecretObfuscator.TryReveal(account.RefreshToken) is not { } refreshToken)
        {
            await NeedsReauthAsync(account, "The stored refresh token is missing or unreadable. Reconnect the account.", ct);
            return null;
        }

        var result = await oauth.RefreshAsync(refreshToken, ct);
        if (!result.Succeeded)
        {
            if (result.IsInvalidGrant)
            {
                // Revoked, or - while the operator's consent screen is still in
                // Testing - simply older than Google's seven-day cap on refresh
                // tokens (docs/plans/kiosk.md, accepted risks).
                await NeedsReauthAsync(account, "Google rejected the stored authorization (invalid_grant). Reconnect the account.", ct);
                return null;
            }

            // Transient as far as we can tell, so the grant is left alone and
            // the next firing tries again.
            account.LastSyncError = $"Google token refresh failed: {result.Error}";
            await db.SaveChangesAsync(ct);
            return null;
        }

        var tokens = result.Tokens;
        account.AccessToken = SecretObfuscator.Obfuscate(tokens.AccessToken);
        account.AccessTokenExpiresAt = tokens.ExpiresAt;
        // Google normally omits refresh_token on a refresh; when it does send a
        // replacement, the one we hold is on its way out.
        if (tokens.RefreshToken is { } rotated)
            account.RefreshToken = SecretObfuscator.Obfuscate(rotated);
        account.LastSyncError = null;
        await db.SaveChangesAsync(ct);

        return tokens.AccessToken;
    }

    private async Task NeedsReauthAsync(EfCalendarAccount account, string reason, CancellationToken ct)
    {
        logger.LogWarning("Calendar account {AccountId} needs re-authorization: {Reason}", account.Id, reason);
        account.NeedsReauth = true;
        account.LastSyncError = reason;
        // The cached token is worthless without a grant behind it, and leaving
        // it would let a reconnect look successful while it was still in play.
        account.AccessToken = null;
        account.AccessTokenExpiresAt = null;
        await db.SaveChangesAsync(ct);
    }
}
