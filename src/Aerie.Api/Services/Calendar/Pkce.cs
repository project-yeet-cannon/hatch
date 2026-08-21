using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Aerie.Api.Services.Calendar;

/// <summary>
/// The two random values an authorization request carries: the PKCE
/// verifier/challenge pair, and the anti-forgery <c>state</c> that comes back
/// on the callback. They share a generator because they want the same thing -
/// 256 bits from a CSPRNG, rendered in the base64url alphabet OAuth allows
/// unescaped in a query string.
/// </summary>
public static class Pkce
{
    /// <summary>A fresh code verifier. 32 random bytes render as 43 base64url characters, inside RFC 7636's 43-128 range.</summary>
    public static string NewVerifier() => RandomBase64Url(32);

    /// <summary>A fresh <c>state</c> value. Not PKCE, but the same shape and the same requirement: unguessable and URL-safe.</summary>
    public static string NewState() => RandomBase64Url(32);

    /// <summary>The S256 challenge for a verifier - base64url(SHA-256(ASCII(verifier))), per RFC 7636.</summary>
    public static string ChallengeFor(string verifier) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string RandomBase64Url(int byteCount) =>
        WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
}
