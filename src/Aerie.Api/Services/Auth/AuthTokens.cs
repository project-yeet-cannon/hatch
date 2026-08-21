using System.Security.Cryptography;
using System.Text;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.WebUtilities;

namespace Aerie.Api.Services.Auth;

/// <summary>
/// The two secrets the wall deals in, and the only place either is generated,
/// hashed, formatted or parsed.
///
/// They are deliberately different shapes because they are handled by different
/// things. A grant token is read by a browser and nothing else, so it is 256
/// bits of base64url and as long as it likes. An invite code is read aloud
/// across a room and typed on a tablet's soft keyboard, so it is eight
/// Crockford characters - the same alphabet and the same fold-on-parse rules as
/// <see cref="Aerie.Api.Modules.Storage.CrateCode"/>, for the same reason: a
/// character a person can mistake is a character that sends them nowhere.
///
/// Both are stored only as SHA-256. Plain SHA-256 rather than a password KDF is
/// right for the token - the input is full-entropy random, so there is nothing
/// for iteration count to defend - and is an accepted, bounded compromise for
/// the code, whose 40 bits are protected by a minutes-long TTL and single use
/// instead (see EfAuthInvite).
/// </summary>
public static class AuthTokens
{
    /// <summary>Bytes of entropy in a grant token. 256 bits, matching Pkce's generator and for the same reason.</summary>
    public const int TokenBytes = 32;

    /// <summary>Characters in an invite code. Eight of Crockford's 32 is ~40 bits - short enough to say out loud, behind a TTL and a rate limiter.</summary>
    public const int InviteCodeLength = 8;

    /// <summary>Crockford's alphabet: 0-9 then A-Z without I, L, O and U.</summary>
    public const string InviteAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>What a formatted code leads with, so someone looking at eight characters on a screen knows what they are for.</summary>
    public const string InvitePrefix = "AERIE";

    private const int InviteGroupSize = InviteCodeLength / 2;

    /// <summary>A fresh grant token. Rendered base64url so it survives a Set-Cookie header without escaping.</summary>
    public static string NewToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>A fresh invite code, bare and uppercase. Cryptographic RNG so a batch of codes doesn't correlate.</summary>
    public static string NewInviteCode() => RandomNumberGenerator.GetString(InviteAlphabet, InviteCodeLength);

    /// <summary>"K3M9P2QT" -> "AERIE-K3M9-P2QT". Presentation only; the bare form is what gets hashed.</summary>
    public static string FormatInviteCode(string code) =>
        code.Length == InviteCodeLength
            ? $"{InvitePrefix}-{code[..InviteGroupSize]}-{code[InviteGroupSize..]}"
            : code;

    /// <summary>
    /// Turns whatever was typed, pasted or scanned into the bare stored form,
    /// or null if it can't be one. Accepts the full "AERIE-K3M9-P2QT" a paste
    /// carries, and applies Crockford's substitutions (I/L -> 1, O -> 0) so a
    /// code someone heard as "eye" and typed as I still lands.
    /// </summary>
    public static string? NormalizeInviteCode(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        Span<char> stripped = stackalloc char[InvitePrefix.Length + InviteCodeLength];
        var kept = 0;

        foreach (var raw in input)
        {
            if (raw is '-' or ' ' or '_') continue;
            if (kept == stripped.Length) return null;
            stripped[kept++] = char.ToUpperInvariant(raw);
        }

        // The prefix comes off before the Crockford fold, not after: "AERIE"
        // contains an I, and folding first would turn it into "AER1E" and stop
        // matching itself.
        var body = stripped[..kept];
        if (body.StartsWith(InvitePrefix)) body = body[InvitePrefix.Length..];
        if (body.Length != InviteCodeLength) return null;

        Span<char> code = stackalloc char[InviteCodeLength];
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i] switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                var other => other,
            };

            if (!InviteAlphabet.Contains(c)) return null;
            code[i] = c;
        }

        return new string(code);
    }

    /// <summary>SHA-256 of a secret, as it is stored. ASCII because both secrets are drawn from ASCII alphabets by construction.</summary>
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    /// <summary>
    /// Fixed-time comparison of two stored hashes. The index lookup that found
    /// the row already leaked nothing (it matched on a hash, not on the
    /// secret), so this is belt to that suspenders - but it costs one call and
    /// it means no code path in the wall compares credential material with ==.
    /// </summary>
    public static bool Matches(byte[]? stored, byte[] presented) =>
        stored is not null
        && stored.Length == AuthHash.Length
        && presented.Length == AuthHash.Length
        && CryptographicOperations.FixedTimeEquals(stored, presented);
}
