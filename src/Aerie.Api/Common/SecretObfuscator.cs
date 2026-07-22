using System.Text;

namespace Aerie.Api.Common;

/// <summary>
/// Reversible XOR obfuscation for secrets stored in SiteSettings - not
/// encryption, just enough that the token isn't sitting in the DB as
/// cleartext. Fine for a value stored in a protected, local database.
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

    private static void Xor(byte[] data)
    {
        for (var i = 0; i < data.Length; i++)
            data[i] ^= Key[i % Key.Length];
    }
}
