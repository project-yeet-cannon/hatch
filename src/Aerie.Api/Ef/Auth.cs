using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

/// <summary>
/// How a grant's holder behaves, which is the only thing anything branches on
/// today. Interactive is a person's browser; Device is a kiosk tablet or
/// anything else that will never see a sign-in screen again after enrollment.
/// Append-only, like every other enum in this folder - see DeviceChannelMetric.
/// </summary>
public enum AuthGrantKind { Interactive, Device }

/// <summary>
/// One enrolled device's long-lived credential - the thing the wall checks on
/// every request. Created by redeeming an <see cref="EfAuthInvite"/>, revoked
/// by deleting the row, and never expires unless ExpiresAt says otherwise.
///
/// Only the SHA-256 of the token is here: the token itself is shown once, at
/// redemption, and lives after that only in the holder's cookie jar. A stolen
/// database therefore yields no usable credential, and revocation is a DELETE
/// rather than a key rotation - see docs/auth-architecture.md's credential-format
/// decision for why this is an opaque token and not a JWT or an auth cookie.
///
/// PersonId is deliberately absent. Grants become people when people exist;
/// adding a nullable column later is cheaper than a nullable FK to a table
/// that hasn't been designed.
/// </summary>
[Table("AuthGrants")]
[Index(nameof(TokenHash), IsUnique = true)]
public class EfAuthGrant
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the grant token. Unique so a lookup is an index seek rather than a scan on the hottest path in the app.</summary>
    [MaxLength(AuthHash.Length)]
    public required byte[] TokenHash { get; set; }

    /// <summary>What the Sessions page calls this device ("Ada's iPhone"). Free text an admin or the sign-in shell chose - the list is unusable if it's a wall of user agents.</summary>
    public required string Label { get; set; }

    public required AuthGrantKind Kind { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this grant was last seen at the wall. Written at most once per
    /// AuthOptions.LastSeenThrottleSeconds - unthrottled it would be a write
    /// on every request through the gate, which is every request in the app.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Client IP at the last write of LastSeenAt, as UseForwardedHeaders resolved it. Telemetry for the Sessions page, not an authorization input.</summary>
    public string? LastSeenIp { get; set; }

    /// <summary>The user agent at enrollment, kept because it's the only evidence of what a device *was* once someone renames the label.</summary>
    public string? UserAgent { get; set; }

    /// <summary>Null means "until revoked", which is the default and the whole point. A value here is a deliberately temporary grant.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// When the browser was last handed a cookie for this grant. Chrome caps
    /// cookie Max-Age at 400 days no matter what we send, so a permanent
    /// cookie doesn't exist; the gate re-issues once this is older than
    /// AuthOptions.GrantRenewAfterDays, which keeps an in-use device signed in
    /// forever and lets a device untouched for a year lapse.
    /// </summary>
    public required DateTimeOffset CookieIssuedAt { get; set; }
}

/// <summary>
/// One outstanding enrollment code - the ceremony by which a device acquires a
/// grant. Short-lived and single use, modeled on <see cref="EfOAuthState"/>:
/// same shape, same opportunistic sweep of expired rows when a new one is
/// created, no job.
///
/// The code is a household secret read aloud across a room, so it's short - 40
/// bits - and only its SHA-256 is stored. That hash is brute-forceable in a way
/// a grant token's is not, which is exactly why the TTL is minutes and
/// redemption is once: by the time an offline attacker has the code, the row it
/// unlocks is gone.
/// </summary>
[Table("AuthInvites")]
[Index(nameof(CodeHash), IsUnique = true)]
public class EfAuthInvite
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [MaxLength(AuthHash.Length)]
    public required byte[] CodeHash { get; set; }

    /// <summary>Optional label to pre-fill on the grant this becomes, for when the admin knows whose phone it's for before they hand the code over.</summary>
    public string? Label { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>Short (minutes). Expired rows are swept opportunistically when a new invite is created, rather than by a job.</summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set exactly once, by the redemption that consumed this invite.
    /// Concurrency-checked so the UPDATE that sets it carries its own
    /// "AND RedeemedAt IS NULL" - two replicas racing the same code produce one
    /// grant and one DbUpdateConcurrencyException, not two grants.
    /// </summary>
    [ConcurrencyCheck]
    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>The grant this invite became. Not an FK: revoking a grant must not have to reason about the spent invite that produced it.</summary>
    public Guid? RedeemedGrantId { get; set; }

    /// <summary>
    /// True for the invite the migrate Job mints when the grant table is empty.
    /// Distinguished because it's the one code ever written to a log, and
    /// because "is there already an unredeemed way in" is what stops a second
    /// deploy minting a second one.
    /// </summary>
    public bool IsBootstrap { get; set; }
}

/// <summary>Size of the SHA-256 digests stored above, named once so the columns and the code that fills them can't disagree.</summary>
public static class AuthHash
{
    public const int Length = 32;
}
