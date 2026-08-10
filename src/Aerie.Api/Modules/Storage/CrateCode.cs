using System.Security.Cryptography;

namespace Aerie.Api.Modules.Storage;

/// <summary>
/// The crate label alphabet and its formatting/parsing rules.
/// </summary>
/// <remarks>
/// Crockford base32 - the digits plus the letters, minus <c>I</c>, <c>L</c>,
/// <c>O</c> and <c>U</c>. The first three are dropped because they're
/// indistinguishable from 1/1/0 on a label that has been in a garage for a
/// decade; <c>U</c> is dropped so a random six characters can't spell something
/// unfortunate. The code is printed as text beside the QR precisely so a scuffed
/// label is still recoverable by typing, which only works if the characters a
/// person types can't be ambiguous.
/// <para>
/// 32^6 is about 1.07 billion codes, so a household will never see a collision;
/// <see cref="StorageService"/> retries on one anyway rather than surfacing a
/// unique-violation to someone standing in the garage with a label printer.
/// </para>
/// </remarks>
public static class CrateCode
{
    public const int Length = 6;

    /// <summary>Crockford's alphabet: 0-9 then A-Z without I, L, O, U.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Where the display dash goes, splitting the code into two even halves.</summary>
    public const int GroupSize = Length / 2;

    /// <summary>A new random code, bare and uppercase. Cryptographic RNG so batches don't correlate.</summary>
    public static string Next() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>"ABC123" -> "ABC-123". Presentation only; the dash is never stored.</summary>
    public static string Format(string code) =>
        code.Length == Length ? $"{code[..GroupSize]}-{code[GroupSize..]}" : code;

    /// <summary>
    /// Turns whatever someone typed or scanned into the stored form, or null if it
    /// can't be one. Applies Crockford's substitutions (I/L -> 1, O -> 0) so a
    /// person reading letters off a faded label lands on the right crate anyway.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        Span<char> code = stackalloc char[Length];
        var written = 0;

        foreach (var raw in input)
        {
            if (raw is '-' or ' ') continue;
            if (written == Length) return null;

            var c = char.ToUpperInvariant(raw) switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                var other => other,
            };

            if (!Alphabet.Contains(c)) return null;
            code[written++] = c;
        }

        return written == Length ? new string(code) : null;
    }
}
