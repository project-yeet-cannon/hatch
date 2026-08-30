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
    bool IsCurrent,
    // Whose device this is, or null for one nobody has claimed. The name rides
    // along with the id because the Sessions page renders it in a cell and
    // would otherwise have to join two lists client-side to draw one column.
    Guid? PersonId = null,
    string? PersonName = null)
{
    public static AuthGrantDto From(EfAuthGrant grant, bool isCurrent) => new(
        grant.Id,
        grant.Label,
        grant.Kind,
        grant.CreatedAt,
        grant.LastSeenAt,
        grant.LastSeenIp,
        grant.UserAgent,
        isCurrent,
        grant.PersonId,
        // Null when the grant was loaded without its owner - `me` and the
        // redemption response both build a DTO from a grant they have in hand
        // rather than from a query that joined. Those two callers describe the
        // device to itself, where the owner is not the question being asked.
        grant.Person?.Name);
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
/// <param name="PersonId">
/// Who this device will belong to, if the operator already knows - the link is
/// then set by the redemption rather than as a second step on the Sessions page
/// afterwards. Optional, and a person deleted before the code is read out
/// simply leaves the grant unclaimed (AuthService.RedeemAsync).
/// </param>
public record CreateInviteRequest(string? Label, Guid? PersonId = null);

/// <summary>
/// Claims a device for a person, or unclaims it. A record with one nullable
/// field rather than a bare Guid in the URL, precisely so that "nobody" is
/// something the API can be *told* - a DELETE would say the same thing in a
/// second endpoint, and unlinking is not a deletion of anything.
/// </summary>
public record LinkPersonRequest(Guid? PersonId);

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
