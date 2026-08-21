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

/// <summary>
/// What an admin asks for when they want to enrol someone. The label is the
/// name the grant will carry if the person redeeming doesn't supply one - "Ada's
/// iPhone" typed by the operator who is about to hand the code over.
/// </summary>
public record CreateInviteRequest(string? Label);

/// <summary>
/// A freshly minted invite, on its way to a QR code and a screen.
///
/// This is the only response in the app that carries a live credential, and it
/// carries it exactly once: the code is not stored, so it cannot be re-shown,
/// and a lost one is replaced by minting another rather than looking this one
/// up. Which is also why it is not logged - see AuthService.CreateInviteAsync,
/// and the one deliberate exception in the migrate Job's bootstrap.
/// </summary>
/// <param name="RedeemPath">
/// The rooted path a scanned QR should open - <c>/apps/auth/r/{code}</c>. Sent
/// as a path rather than an absolute URL because the host a QR must carry is
/// the install's canonical one (<c>Apps:PublicBaseUrl</c>), which the client
/// already knows how to ask for and how to fall back from.
/// </param>
public record AuthInviteDto(
    Guid Id,
    string Code,
    string FormattedCode,
    string RedeemPath,
    DateTimeOffset ExpiresAt,
    string? Label);
