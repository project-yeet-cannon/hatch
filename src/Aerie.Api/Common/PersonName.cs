using System.Globalization;
using System.Text;

namespace Aerie.Api.Common;

/// <summary>
/// The one place a person's name is turned from request text into a value the
/// database is allowed to hold.
///
/// The brief was "allow nearly anything - this is a home, not an office", and
/// that is what this does: emoji, scripts, punctuation, a name that is entirely
/// 🦖. What it does not allow is the small set of characters that are not names
/// at all but instructions to the thing displaying them.
///
/// **On the sanitization you would expect and will not find here.** There is no
/// SQL escaping, because every write goes through EF Core's parameterized
/// commands - an escaping pass would be a second, weaker defence in front of a
/// structural one, and the failure mode of that pattern is that someone later
/// concatenates a query believing this class made it safe. There is no HTML
/// escaping either: React escapes at the point of render, and a name stored
/// pre-escaped is a name that reads <c>Bob&amp;amp;Alice</c> the second time
/// someone edits it. Store the truth; escape at the boundary that needs it.
///
/// What is left is the part neither of those covers:
///
/// - **Control characters** (C0/C1). A newline in a name breaks every log line
///   that carries it and every table that renders it, and no one's name has one.
/// - **Bidi and other invisible format characters** (Unicode <c>Cf</c>). This is
///   the Trojan Source class: <c>U+202E</c> reverses the text after it, so a
///   name can rearrange the sentence it appears inside. Zero-width joiners are
///   the deliberate exception - they are load-bearing inside emoji sequences,
///   and dropping them turns 👨‍👩‍👧‍👦 into four separate people.
/// - **NFC normalization**, so that the two Unicode spellings of "José" are one
///   string. Without it two people can have names that are equal on screen and
///   different in the database, which is the kind of bug that gets diagnosed as
///   "the search is broken".
/// - **Whitespace collapse**, because a name is not a layout.
///
/// The length rule is counted in grapheme clusters - what a reader would call
/// characters - rather than in the UTF-16 code units the column is measured in.
/// 🇺🇸 is one thing to a person and two chars to .NET; 👨‍👩‍👧‍👦 is one thing and
/// eleven. Counting code units would mean a name that looks short being refused
/// for being long, which is unexplainable to whoever is standing at the form.
/// <see cref="MaxChars"/> is the column's own bound and exists so the schema is
/// finite even for a name made entirely of the most expensive graphemes there
/// are; a name can only hit it by trying to.
/// </summary>
public static class PersonName
{
    /// <summary>What a reader would count. Long enough for a full name with titles, short enough to render in a table cell.</summary>
    public const int MaxGraphemes = 60;

    /// <summary>The column's bound, in UTF-16 chars. Four times <see cref="MaxGraphemes"/> - enough headroom for a name of nothing but flags and family emoji, and still a finite column.</summary>
    public const int MaxChars = 240;

    public const string EmptyError = "A person needs a name.";
    /// <summary>Static readonly rather than const because it quotes <see cref="MaxGraphemes"/>, and a message with the number typed into it is a message that drifts from the rule it describes.</summary>
    public static readonly string TooLongError = $"A name can be at most {MaxGraphemes} characters.";

    /// <summary>The one format character that survives: it is what holds a multi-part emoji together.</summary>
    private const char ZeroWidthJoiner = '\u200D';

    /// <summary>
    /// Normalizes <paramref name="raw"/> into a storable name, or explains why
    /// it cannot be one. Never throws: a bad name is an answer, not an
    /// exception, because the caller is always a controller turning it into a
    /// 400 with that sentence in it.
    /// </summary>
    public static bool TryNormalize(string? raw, out string name, out string error)
    {
        name = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = EmptyError;
            return false;
        }

        // NFC first, so the length is measured on the same string that will be
        // stored - a decomposed name can compose down to fewer graphemes, and
        // measuring before normalizing would refuse names that fit.
        var normalized = raw.Normalize(NormalizationForm.FormC);

        var builder = new StringBuilder(normalized.Length);
        var pendingSpace = false;

        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                // Collapsed rather than kept: any run of spacing, of any width,
                // becomes one ordinary space. Leading runs are dropped because
                // nothing has been written yet.
                pendingSpace = builder.Length > 0;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (category == UnicodeCategory.Format && rune.Value != ZeroWidthJoiner)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune);
        }

        // A trailing pendingSpace is simply never written - that is the trim.
        var result = builder.ToString();

        // Reachable with input that was not blank: a string of nothing but
        // control characters normalizes to nothing at all.
        if (result.Length == 0)
        {
            error = EmptyError;
            return false;
        }

        if (CountGraphemes(result) > MaxGraphemes || result.Length > MaxChars)
        {
            error = TooLongError;
            return false;
        }

        name = result;
        return true;
    }

    /// <summary>
    /// Counts what a person would call characters. Public because the same
    /// count is the only honest way to describe the limit anywhere else - a
    /// second implementation of "how long is this name" is a second answer.
    /// </summary>
    public static int CountGraphemes(string value)
    {
        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) count++;
        return count;
    }
}
