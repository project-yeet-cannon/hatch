using System.Text;

namespace Aerie.Api.Common;

/// <summary>
/// Reversible XOR obfuscation - not encryption, just enough that a token isn't
/// sitting in the DB as cleartext.
///
/// This is scheme <c>v1</c>, and it is an implementation detail of
/// <see cref="SecretProtector"/>: call that instead. Everything stored goes
/// through the protector so that the day this is replaced with real crypto,
/// the replacement is a new scheme beside this one rather than an edit to
/// every call site that ever wrote a secret.
/// </summary>
public static class SecretObfuscator
{
    private static readonly byte[] Key = "Aerie.SiteSettings.Obfuscation.v1"u8.ToArray();

    public static string Obfuscate(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        Xor(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static string Deobfuscate(string obfuscated)
    {
        var bytes = Convert.FromBase64String(obfuscated);
        Xor(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Deobfuscates a stored secret, treating one that won't decode - absent,
    /// blank, or hand-edited into something that isn't base64 - as absent
    /// rather than as an exception. Callers that hold obfuscated columns
    /// (calendar tokens) want a missing value to cost that one account a
    /// reconnect, not to throw out of every operation that touches it.
    /// </summary>
    public static string? TryReveal(string? obfuscated)
    {
        if (string.IsNullOrWhiteSpace(obfuscated)) return null;
        try
        {
            var plaintext = Deobfuscate(obfuscated);
            return string.IsNullOrWhiteSpace(plaintext) ? null : plaintext;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Xor(byte[] data)
    {
        for (var i = 0; i < data.Length; i++)
            data[i] ^= Key[i % Key.Length];
    }
}
