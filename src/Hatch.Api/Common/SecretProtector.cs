using System.Text;

namespace Hatch.Api.Common;

/// <summary>
/// The one way a secret is written to Hatch's database, and the one way it is
/// read back (docs/camera-devices-architecture.md).
///
/// Every protected value carries the scheme that produced it:
/// <c>v1:&lt;payload&gt;</c>. That prefix is the whole point of this type. The
/// protection today is <see cref="SecretObfuscator"/>'s XOR - obfuscation, not
/// encryption, honestly labelled as such in docs/secrets-architecture.md - and
/// it will not be forever. When it is replaced, the replacement is a new
/// scheme registered here plus a re-protect on next write; rows in both formats
/// stay readable throughout, so the migration never has to be a single
/// stop-the-world pass over every table that holds a secret.
///
/// The discriminator is free rather than clever: a v1 payload is base64, base64
/// has no colon in its alphabet, so no legacy value written before this type
/// existed can be mistaken for a tagged one. That is why <see cref="Unprotect"/>
/// can read an untagged value as v1 without a flag, a column, or a migration
/// that must run before the code that depends on it.
/// </summary>
public static class SecretProtector
{
    /// <summary>
    /// The scheme new values are written with. One symbol to change - plus a
    /// case in <see cref="Unprotect"/> - when real crypto lands.
    /// </summary>
    public const string CurrentScheme = "v1";

    private const char SchemeSeparator = ':';

    /// <summary>
    /// Wraps <paramref name="plaintext"/> in the current scheme. An empty input
    /// stays empty and untagged: "no secret" is a state the callers already
    /// have (a cleared form field, an unset setting), and tagging it would turn
    /// every absent value into a present one that decodes to nothing.
    /// </summary>
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        return $"{CurrentScheme}{SchemeSeparator}{SecretObfuscator.Obfuscate(plaintext)}";
    }

    /// <summary>
    /// The plaintext behind a stored value, or null when there isn't one -
    /// absent, blank, or damaged past reading.
    ///
    /// Null rather than an exception for the reason <see cref="SecretObfuscator.TryReveal"/>
    /// gives: a value that has been hand-edited in the database should cost the
    /// one account or camera it belongs to a reconnect, not throw out of every
    /// operation that happens to touch the row.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        var separator = stored.IndexOf(SchemeSeparator);
        if (separator < 0)
        {
            // Untagged: written before this type existed. v1 is that same
            // algorithm, so it reads without conversion - which is what lets
            // the data migration be a labelling pass rather than a re-encrypt,
            // and what lets code deployed ahead of that migration still work.
            return SecretObfuscator.TryReveal(stored);
        }

        var scheme = stored[..separator];
        var payload = stored[(separator + 1)..];

        return scheme switch
        {
            "v1" => SecretObfuscator.TryReveal(payload),
            // An unknown scheme is a value written by a *newer* Hatch than this
            // one - a rolled-back deployment reading a row the new version
            // wrote. Unreadable is the honest answer, and it is the same
            // answer as damaged, which callers already handle.
            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="stored"/> holds something. Distinguishes "the
    /// operator has set a password" from "the operator has not" without
    /// revealing or even decoding it - which is what an admin API needs to
    /// answer, since it must never hand the value back.
    /// </summary>
    public static bool HasValue(string? stored) => !string.IsNullOrWhiteSpace(stored);

    /// <summary>
    /// Re-labels an untagged legacy value as v1 without decoding it. The bytes
    /// are already v1's bytes; only the tag is missing. Used by the data
    /// migration and by nothing else - ordinary writes go through
    /// <see cref="Protect"/>.
    /// </summary>
    public static string Retag(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;
        if (stored.Contains(SchemeSeparator)) return stored;
        return $"{CurrentScheme}{SchemeSeparator}{stored}";
    }
}
