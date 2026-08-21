using Aerie.Api.Ef;

namespace Aerie.Api.Models.Auth;

/// <summary>
/// One enrolled device as the sign-in shell and the admin Sessions page see it.
/// The token is not here and never will be: it exists in plaintext exactly once,
/// in the Set-Cookie header of the redemption that minted it.
/// </summary>
public record AuthGrantDto(
    Guid Id,
    string Label,
    AuthGrantKind Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    string? LastSeenIp,
    string? UserAgent,
    // True for the grant the caller is holding - what stops someone revoking
    // their own way out of the room.
    bool IsCurrent)
{
    public static AuthGrantDto From(EfAuthGrant grant, bool isCurrent) => new(
        grant.Id,
        grant.Label,
        grant.Kind,
        grant.CreatedAt,
        grant.LastSeenAt,
        grant.LastSeenIp,
        grant.UserAgent,
        isCurrent);
}

/// <summary>
/// What the sign-in shell posts. <paramref name="Label"/> is the device name
/// the person confirmed ("Ada's iPhone"), pre-filled from the user agent so the
/// Sessions list is legible instead of a wall of Mozilla/5.0.
/// </summary>
public record RedeemRequest(string? Code, string? Label);

/// <summary>A refusal the shell can turn into a sentence - see AuthRedemption's error constants.</summary>
public record AuthErrorDto(string Error);
