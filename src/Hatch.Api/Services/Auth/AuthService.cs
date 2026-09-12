using Hatch.Api.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Hatch.Api.Services.Auth;

/// <summary>
/// A freshly minted invite. <paramref name="Code"/> is the only time the code
/// exists outside a hash - it is returned to whoever asked for it and then
/// forgotten, so it can be shown, printed into a QR, or (for the bootstrap
/// invite alone) logged.
/// </summary>
public record AuthInviteCreated(Guid Id, string Code, DateTimeOffset ExpiresAt, string? Label)
{
    /// <summary>The code as a human should see it - "HATCH-K3M9-P2QT".</summary>
    public string FormattedCode => AuthTokens.FormatInviteCode(Code);
}

/// <summary>
/// The answer to a redemption: a grant and its one-time token, or the reason
/// there isn't one. The reasons are distinguished because the sign-in shell
/// says them out loud - "that code expired" and "that isn't a code" send a
/// person to different next actions, and neither tells an attacker anything a
/// 15-minute single-use code hadn't already conceded.
/// </summary>
/// <summary>
/// A grant that verified, and the token that carried it. The token is not
/// incidental: the sliding cookie re-issue has to write back the same secret,
/// and once a request can present more than one, "the token" is no longer
/// something the caller already knows.
/// </summary>
public record AuthVerification(EfAuthGrant Grant, string Token);

/// <summary>
/// How linking a device to a person went. Three outcomes rather than a bool
/// because the two failures are different sentences to whoever is standing at
/// the Sessions page: a missing grant means the list is stale, a missing person
/// means the dropdown is.
/// </summary>
public enum GrantLinkResult { Linked, NoSuchGrant, NoSuchPerson }

public record AuthRedemption(EfAuthGrant? Grant, string? Token, string? Error)
{
    public const string InvalidCode = "invalid_code";
    public const string Expired = "expired";
    public const string AlreadyRedeemed = "already_redeemed";

    public bool Succeeded => Error is null;

    public static AuthRedemption Failed(string error) => new(null, null, error);
}

/// <summary>
/// A freshly minted API key. <paramref name="Secret"/> is the only time the key
/// exists outside a hash - it goes into the response, onto the operator's
/// screen once, and is then unrecoverable. A lost key is replaced by minting
/// another.
/// </summary>
public record ApiKeyCreated(EfApiKey Key, string Secret);

public interface IAuthService
{
    /// <summary>
    /// Mints an invite, sweeping expired ones on the way through (the
    /// EfOAuthState pattern - no job). The returned code is not recoverable
    /// afterwards; only its hash is stored.
    /// </summary>
    Task<AuthInviteCreated> CreateInviteAsync(string? label, Guid? personId, bool isBootstrap, CancellationToken ct);

    /// <summary>Turns a code into a grant. Never throws on a bad code - the reason comes back on the result.</summary>
    Task<AuthRedemption> RedeemAsync(string? code, string? label, string? userAgent, string? clientIp, CancellationToken ct);

    /// <summary>
    /// The hot path: the grant a token belongs to, or null if there isn't a
    /// live one. Also keeps LastSeenAt/LastSeenIp current, throttled to
    /// AuthOptions.LastSeenThrottleSeconds.
    /// </summary>
    Task<AuthVerification?> VerifyAsync(IReadOnlyList<string> tokens, string? clientIp, CancellationToken ct);

    /// <summary>
    /// The key a bearer secret belongs to, or null if no live row answers to
    /// it - never minted, mistyped, or revoked, which are deliberately the same
    /// answer here for the same reason a bad grant token is.
    ///
    /// Keeps <see cref="EfApiKey.LastUsedAt"/> current, throttled exactly as a
    /// grant's last-seen is: this is the hot path for every request an agent
    /// makes, and an unthrottled write on it would be a write per request.
    /// </summary>
    Task<EfApiKey?> VerifyApiKeyAsync(string? secret, CancellationToken ct);

    /// <summary>Every key, newest first, for the admin API Keys page - revoked ones included, because a revocation is a fact worth seeing.</summary>
    Task<IReadOnlyList<EfApiKey>> ListApiKeysAsync(CancellationToken ct);

    /// <summary>
    /// Mints a key. The returned secret is the only time it exists outside a
    /// hash, exactly as for an invite code - it is shown once and then gone.
    /// Returns null when the name is already taken.
    /// </summary>
    Task<ApiKeyCreated?> CreateApiKeyAsync(string name, IReadOnlyList<string> scopes, CancellationToken ct);

    /// <summary>
    /// Stops a key working. Returns false if there was no such key, which a
    /// caller turns into a 404; revoking an already-revoked key is a no-op
    /// rather than a refusal - the caller wanted it off and it is off.
    /// </summary>
    Task<bool> RevokeApiKeyAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Records that the browser was just handed a fresh cookie for this grant,
    /// which is what restarts the sliding window. Separate from VerifyAsync
    /// because only the in-process middleware can actually issue one - Traefik
    /// copies only the headers named in authResponseHeaders back from the
    /// forwardAuth response, and Set-Cookie is not among them.
    /// </summary>
    Task MarkCookieIssuedAsync(EfAuthGrant grant, CancellationToken ct);

    /// <summary>Every grant, newest first, for the admin Sessions page.</summary>
    Task<IReadOnlyList<EfAuthGrant>> ListGrantsAsync(CancellationToken ct);

    /// <summary>Revocation is a DELETE. Returns false if there was no such grant, which a caller turns into a 404.</summary>
    Task<bool> RevokeGrantAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Claims a device for a person, or unclaims it with a null
    /// <paramref name="personId"/>. The single write path for the link - the
    /// People page reads it and does not set it, because one FK with two forms
    /// writing it is two forms that can disagree about what they last saw.
    /// </summary>
    Task<GrantLinkResult> SetGrantPersonAsync(Guid grantId, Guid? personId, CancellationToken ct);

    /// <summary>Whether this install has any way in at all - what the migrate Job checks before minting a bootstrap invite.</summary>
    Task<bool> HasAnyAccessAsync(CancellationToken ct);
}

/// <summary>
/// Everything that reads or writes a grant. The ceremony that hands one out is
/// deliberately not in here - an invite code today, a pending-approval queue or
/// a passkey later, all producing the same row (docs/auth-architecture.md).
/// </summary>
public class AuthService(
    AppDbContext db,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<AuthService> logger) : IAuthService
{
    private readonly AuthOptions options = options.Value;

    /// <summary>Codes are ~40 bits, so a collision is a rounding error away from impossible - but a unique index turns one into a 500 for whoever was standing there, and a retry costs three lines.</summary>
    private const int CodeCollisionRetries = 3;

    public async Task<AuthInviteCreated> CreateInviteAsync(string? label, Guid? personId, bool isBootstrap, CancellationToken ct)
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
                PersonId = personId,
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

        // The invite's person, if they are still here. Checked rather than
        // trusted because EfAuthInvite.PersonId is deliberately not a foreign
        // key: a person deleted between minting a code and reading it out must
        // leave the code redeemable, so a stale id links nothing and the device
        // still gets in.
        Guid? personId = invite.PersonId is { } invitedPerson && await db.People.AnyAsync(p => p.Id == invitedPerson, ct)
            ? invitedPerson
            : null;

        var token = AuthTokens.NewToken();
        var grant = new EfAuthGrant
        {
            TokenHash = AuthTokens.Hash(token),
            Label = FirstNonBlank(label, invite.Label) ?? "Unnamed device",
            PersonId = personId,
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

    /// <summary>
    /// The first of the presented tokens that resolves to a live grant, with
    /// the token that did it - the caller needs to know *which* one, because
    /// the sliding re-issue has to write back the same secret.
    ///
    /// Plural because a browser can present several cookies of one name at once
    /// (see <see cref="AuthCookie.ReadAll"/>). Taking only the first, or only
    /// the last, is how a stale duplicate locks a device out of a host while it
    /// holds a perfectly good grant. The loop stops on the first match, so the
    /// ordinary one-cookie request is one query, exactly as before.
    /// </summary>
    public async Task<AuthVerification?> VerifyAsync(IReadOnlyList<string> tokens, string? clientIp, CancellationToken ct)
    {
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token)) continue;

            var hash = AuthTokens.Hash(token);
            // The owner comes along on the hot path deliberately. It is a LEFT
            // JOIN on a primary key against a table holding a household's worth
            // of rows, which Postgres answers inside the round trip it was
            // already making - and it is what lets every caller downstream (the
            // Sessions page, a log line) name a human without a second query or
            // a cache to invalidate when someone is renamed.
            var grant = await db.AuthGrants.Include(g => g.Person).FirstOrDefaultAsync(g => g.TokenHash == hash, ct);

            if (grant is null || !AuthTokens.Matches(grant.TokenHash, hash)) continue;

            var now = time.GetUtcNow();
            if (grant.ExpiresAt is { } expiresAt && expiresAt <= now)
            {
                logger.LogWarning("Grant {GrantId} ({Label}) presented after expiry from {ClientIp}", grant.Id, grant.Label, clientIp);
                continue;
            }

            await TouchAsync(grant, clientIp, now, ct);
            return new AuthVerification(grant, token);
        }

        return null;
    }

    public async Task MarkCookieIssuedAsync(EfAuthGrant grant, CancellationToken ct)
    {
        grant.CookieIssuedAt = time.GetUtcNow();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Same reasoning as TouchAsync: the cookie has already been written
            // to the response by the time this runs, so a lost race here costs
            // one early re-issue on the next request rather than a 500 on a
            // request that was authenticated fine.
            logger.LogDebug(ex, "Could not record cookie re-issue for grant {GrantId}", grant.Id);
        }
    }

    public async Task<EfApiKey?> VerifyApiKeyAsync(string? secret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret)) return null;

        var hash = AuthTokens.Hash(secret);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Hash == hash, ct);

        if (key is null || !AuthTokens.Matches(key.Hash, hash)) return null;

        var now = time.GetUtcNow();
        if (!key.IsLive(now))
        {
            // Warning rather than Debug, and the one refusal in this file worth
            // a line on its own: a revoked key still being presented means a
            // program somewhere is still holding it, and that is the operator's
            // cue to go and take it out of whatever file it lives in.
            logger.LogWarning("Revoked API key {KeyId} ({Name}) was presented", key.Id, key.Name);
            return null;
        }

        await TouchKeyAsync(key, now, ct);
        return key;
    }

    public async Task<IReadOnlyList<EfApiKey>> ListApiKeysAsync(CancellationToken ct) =>
        await db.ApiKeys.AsNoTracking().OrderByDescending(k => k.CreatedAt).ToListAsync(ct);

    public async Task<ApiKeyCreated?> CreateApiKeyAsync(string name, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        // Checked rather than left to the unique index, because the index turns
        // a name somebody typed twice into a 500 and this turns it into a
        // sentence. The index is still there underneath as the backstop.
        if (await db.ApiKeys.AnyAsync(k => k.Name == name, ct)) return null;

        var secret = AuthTokens.NewApiKey();
        var key = new EfApiKey
        {
            Name = name,
            Prefix = AuthTokens.ApiKeyPrefixOf(secret),
            Hash = AuthTokens.Hash(secret),
            Scopes = [.. scopes],
            CreatedAt = time.GetUtcNow(),
        };

        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("API key {KeyId} ({Name}) minted with scopes {Scopes}", key.Id, key.Name, key.Scopes);
        return new ApiKeyCreated(key, secret);
    }

    public async Task<bool> RevokeApiKeyAsync(Guid id, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return false;

        // Already revoked stays revoked at the moment it first was. Overwriting
        // the timestamp would quietly rewrite the answer to "when did this stop
        // working", which is the whole reason the column is not a bool.
        if (key.RevokedAt is null)
        {
            key.RevokedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            logger.LogWarning("API key {KeyId} ({Name}) revoked", key.Id, key.Name);
        }

        return true;
    }

    public async Task<IReadOnlyList<EfAuthGrant>> ListGrantsAsync(CancellationToken ct) =>
        await db.AuthGrants.AsNoTracking().Include(g => g.Person).OrderByDescending(g => g.CreatedAt).ToListAsync(ct);

    public async Task<bool> RevokeGrantAsync(Guid id, CancellationToken ct)
    {
        var grant = await db.AuthGrants.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (grant is null) return false;

        db.AuthGrants.Remove(grant);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Grant {GrantId} ({Label}) revoked", grant.Id, grant.Label);
        return true;
    }

    public async Task<GrantLinkResult> SetGrantPersonAsync(Guid grantId, Guid? personId, CancellationToken ct)
    {
        var grant = await db.AuthGrants.Include(g => g.Person).FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (grant is null) return GrantLinkResult.NoSuchGrant;

        if (personId is { } id)
        {
            var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (person is null) return GrantLinkResult.NoSuchPerson;
            grant.Person = person;
        }
        else
        {
            grant.Person = null;
        }

        grant.PersonId = personId;
        await db.SaveChangesAsync(ct);

        // Information rather than Warning, unlike revocation: claiming a device
        // grants nobody anything, since nothing in the app reads this column to
        // decide anything. It is logged at all because "who did this tablet
        // belong to in March" is a question that only has an answer if someone
        // wrote it down.
        logger.LogInformation("Grant {GrantId} ({Label}) linked to person {PersonId}", grant.Id, grant.Label, personId);
        return GrantLinkResult.Linked;
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
    /// <summary>
    /// The key's last-seen, on the same throttle a grant's is and swallowing
    /// the same failure. No client IP: a key is a program, and the address it
    /// dials from says nothing about who is holding it.
    /// </summary>
    private async Task TouchKeyAsync(EfApiKey key, DateTimeOffset now, CancellationToken ct)
    {
        if (key.LastUsedAt is { } lastUsed && now - lastUsed < options.LastSeenThrottle) return;

        key.LastUsedAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogDebug(ex, "Could not update last-used for API key {KeyId}", key.Id);
        }
    }

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
