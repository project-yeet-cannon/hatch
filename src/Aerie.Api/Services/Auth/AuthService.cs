using Aerie.Api.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.Auth;

/// <summary>
/// A freshly minted invite. <paramref name="Code"/> is the only time the code
/// exists outside a hash - it is returned to whoever asked for it and then
/// forgotten, so it can be shown, printed into a QR, or (for the bootstrap
/// invite alone) logged.
/// </summary>
public record AuthInviteCreated(Guid Id, string Code, DateTimeOffset ExpiresAt, string? Label)
{
    /// <summary>The code as a human should see it - "AERIE-K3M9-P2QT".</summary>
    public string FormattedCode => AuthTokens.FormatInviteCode(Code);
}

/// <summary>
/// The answer to a redemption: a grant and its one-time token, or the reason
/// there isn't one. The reasons are distinguished because the sign-in shell
/// says them out loud - "that code expired" and "that isn't a code" send a
/// person to different next actions, and neither tells an attacker anything a
/// 15-minute single-use code hadn't already conceded.
/// </summary>
public record AuthRedemption(EfAuthGrant? Grant, string? Token, string? Error)
{
    public const string InvalidCode = "invalid_code";
    public const string Expired = "expired";
    public const string AlreadyRedeemed = "already_redeemed";

    public bool Succeeded => Error is null;

    public static AuthRedemption Failed(string error) => new(null, null, error);
}

public interface IAuthService
{
    /// <summary>
    /// Mints an invite, sweeping expired ones on the way through (the
    /// EfOAuthState pattern - no job). The returned code is not recoverable
    /// afterwards; only its hash is stored.
    /// </summary>
    Task<AuthInviteCreated> CreateInviteAsync(string? label, bool isBootstrap, CancellationToken ct);

    /// <summary>Turns a code into a grant. Never throws on a bad code - the reason comes back on the result.</summary>
    Task<AuthRedemption> RedeemAsync(string? code, string? label, string? userAgent, string? clientIp, CancellationToken ct);

    /// <summary>
    /// The hot path: the grant a token belongs to, or null if there isn't a
    /// live one. Also keeps LastSeenAt/LastSeenIp current, throttled to
    /// AuthOptions.LastSeenThrottleSeconds.
    /// </summary>
    Task<EfAuthGrant?> VerifyAsync(string? token, string? clientIp, CancellationToken ct);

    /// <summary>Every grant, newest first, for the admin Sessions page.</summary>
    Task<IReadOnlyList<EfAuthGrant>> ListGrantsAsync(CancellationToken ct);

    /// <summary>Revocation is a DELETE. Returns false if there was no such grant, which a caller turns into a 404.</summary>
    Task<bool> RevokeGrantAsync(Guid id, CancellationToken ct);

    /// <summary>Whether this install has any way in at all - what the migrate Job checks before minting a bootstrap invite.</summary>
    Task<bool> HasAnyAccessAsync(CancellationToken ct);
}

/// <summary>
/// Everything that reads or writes a grant. The ceremony that hands one out is
/// deliberately not in here - an invite code today, a pending-approval queue or
/// a passkey later, all producing the same row (docs/plans/auth.md).
/// </summary>
public class AuthService(
    AerieContext db,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly AuthOptions options = options.Value;

    /// <summary>Codes are ~40 bits, so a collision is a rounding error away from impossible - but a unique index turns one into a 500 for whoever was standing there, and a retry costs three lines.</summary>
    private const int CodeCollisionRetries = 3;

    public async Task<AuthInviteCreated> CreateInviteAsync(string? label, bool isBootstrap, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var expiresAt = now + (isBootstrap ? options.BootstrapInviteTtl : options.InviteTtl);

        // Opportunistic sweep, same as a new OAuth flow does: expired invites
        // are unusable by definition, and a household never generates enough of
        // them to be worth a scheduled job.
        var stale = await db.AuthInvites.Where(i => i.ExpiresAt < now).ToListAsync(ct);
        if (stale.Count > 0) db.AuthInvites.RemoveRange(stale);

        for (var attempt = 1; ; attempt++)
        {
            var code = AuthTokens.NewInviteCode();
            var invite = new EfAuthInvite
            {
                CodeHash = AuthTokens.Hash(code),
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                CreatedAt = now,
                ExpiresAt = expiresAt,
                IsBootstrap = isBootstrap,
            };
            db.AuthInvites.Add(invite);

            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Minted {Kind} invite {InviteId} expiring {ExpiresAt}",
                    isBootstrap ? "bootstrap" : "admin", invite.Id, expiresAt);
                return new AuthInviteCreated(invite.Id, code, expiresAt, invite.Label);
            }
            catch (DbUpdateException) when (attempt < CodeCollisionRetries)
            {
                db.Entry(invite).State = EntityState.Detached;
                logger.LogWarning("Invite code collided on attempt {Attempt} - drawing another", attempt);
            }
        }
    }

    public async Task<AuthRedemption> RedeemAsync(string? code, string? label, string? userAgent, string? clientIp, CancellationToken ct)
    {
        if (AuthTokens.NormalizeInviteCode(code) is not { } normalized)
        {
            logger.LogWarning("Invite redemption refused: {Reason} from {ClientIp}", AuthRedemption.InvalidCode, clientIp);
            return AuthRedemption.Failed(AuthRedemption.InvalidCode);
        }

        var hash = AuthTokens.Hash(normalized);
        var invite = await db.AuthInvites.FirstOrDefaultAsync(i => i.CodeHash == hash, ct);

        if (invite is null || !AuthTokens.Matches(invite.CodeHash, hash))
        {
            logger.LogWarning("Invite redemption refused: {Reason} from {ClientIp}", AuthRedemption.InvalidCode, clientIp);
            return AuthRedemption.Failed(AuthRedemption.InvalidCode);
        }

        if (invite.RedeemedAt is not null)
        {
            logger.LogWarning("Invite redemption refused: {Reason} for invite {InviteId} from {ClientIp}", AuthRedemption.AlreadyRedeemed, invite.Id, clientIp);
            return AuthRedemption.Failed(AuthRedemption.AlreadyRedeemed);
        }

        var now = time.GetUtcNow();
        if (invite.ExpiresAt <= now)
        {
            logger.LogWarning("Invite redemption refused: {Reason} for invite {InviteId} from {ClientIp}", AuthRedemption.Expired, invite.Id, clientIp);
            return AuthRedemption.Failed(AuthRedemption.Expired);
        }

        var token = AuthTokens.NewToken();
        var grant = new EfAuthGrant
        {
            TokenHash = AuthTokens.Hash(token),
            Label = FirstNonBlank(label, invite.Label) ?? "Unnamed device",
            Kind = AuthGrantKind.Interactive,
            CreatedAt = now,
            CookieIssuedAt = now,
            LastSeenAt = now,
            LastSeenIp = clientIp,
            UserAgent = Truncate(userAgent, 512),
        };
        db.AuthGrants.Add(grant);

        invite.RedeemedAt = now;
        invite.RedeemedGrantId = grant.Id;

        try
        {
            // One SaveChanges, so the grant and the invite it consumed land in
            // one transaction. RedeemedAt is concurrency-checked, so this
            // UPDATE carries its own "AND RedeemedAt IS NULL" - two replicas
            // racing the same code produce one grant, not two.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning("Invite redemption refused: {Reason} for invite {InviteId} from {ClientIp} (lost the race)", AuthRedemption.AlreadyRedeemed, invite.Id, clientIp);
            return AuthRedemption.Failed(AuthRedemption.AlreadyRedeemed);
        }

        logger.LogInformation("Invite {InviteId} redeemed into grant {GrantId} ({Label}) from {ClientIp}", invite.Id, grant.Id, grant.Label, clientIp);
        return new AuthRedemption(grant, token, null);
    }

    public async Task<EfAuthGrant?> VerifyAsync(string? token, string? clientIp, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return null;

        var hash = AuthTokens.Hash(token);
        var grant = await db.AuthGrants.FirstOrDefaultAsync(g => g.TokenHash == hash, ct);

        if (grant is null || !AuthTokens.Matches(grant.TokenHash, hash)) return null;

        var now = time.GetUtcNow();
        if (grant.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            logger.LogWarning("Grant {GrantId} ({Label}) presented after expiry from {ClientIp}", grant.Id, grant.Label, clientIp);
            return null;
        }

        await TouchAsync(grant, clientIp, now, ct);
        return grant;
    }

    public async Task<IReadOnlyList<EfAuthGrant>> ListGrantsAsync(CancellationToken ct) =>
        await db.AuthGrants.AsNoTracking().OrderByDescending(g => g.CreatedAt).ToListAsync(ct);

    public async Task<bool> RevokeGrantAsync(Guid id, CancellationToken ct)
    {
        var grant = await db.AuthGrants.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (grant is null) return false;

        db.AuthGrants.Remove(grant);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Grant {GrantId} ({Label}) revoked", grant.Id, grant.Label);
        return true;
    }

    public async Task<bool> HasAnyAccessAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.AuthGrants.AnyAsync(ct)
            || await db.AuthInvites.AnyAsync(i => i.RedeemedAt == null && i.ExpiresAt > now, ct);
    }

    /// <summary>
    /// The throttled half of VerifyAsync. Skipping the write when nothing
    /// meaningful changed is what keeps the gate a read on the request path;
    /// the IP is checked too so a device that moved networks shows its new one
    /// without waiting out the window.
    /// </summary>
    private async Task TouchAsync(EfAuthGrant grant, string? clientIp, DateTimeOffset now, CancellationToken ct)
    {
        var due = grant.LastSeenAt is not { } lastSeen
            || now - lastSeen >= options.LastSeenThrottle
            || (clientIp is not null && clientIp != grant.LastSeenIp);
        if (!due) return;

        grant.LastSeenAt = now;
        if (clientIp is not null) grant.LastSeenIp = clientIp;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Telemetry, not authorization. Two replicas touching the same
            // grant in the same instant must not turn a valid request into a
            // 500 over a column nothing branches on.
            logger.LogDebug(ex, "Could not update last-seen for grant {GrantId}", grant.Id);
        }
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.Select(c => c?.Trim()).FirstOrDefault(c => !string.IsNullOrEmpty(c));

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
