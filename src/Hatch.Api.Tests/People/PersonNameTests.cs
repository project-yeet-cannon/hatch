using Hatch.Api.Common;

namespace Hatch.Api.Tests.People;

/// <summary>
/// What a household is allowed to call someone.
///
/// The tests split cleanly in two, and the split is the design: the permissive
/// half proves that the fun names survive - emoji, scripts, a name that is one
/// dinosaur - and the strict half proves that the characters which are not
/// names but instructions do not. A rule that only had the second half would be
/// an office directory.
///
/// Every invisible character below is written as a \u escape rather than
/// pasted. That is not fastidiousness: a test whose input is a zero-width
/// character looks, in a diff, exactly like a test whose input is nothing at
/// all, and the next person to touch this file would have no way to tell which
/// one they were reading.
/// </summary>
public class PersonNameTests
{
    [Theory]
    [InlineData("Adam")]
    [InlineData("Ada Lovelace")]
    [InlineData("Anne-Marie O'Brien")]
    [InlineData("Þorbjörg")]
    [InlineData("张伟")]
    [InlineData("🦖")]
    [InlineData("Dad 🎸")]
    // The whole point of the grapheme rule: a flag, a family, and a skin tone
    // that would each be several chars to a naive length check.
    [InlineData("🇺🇸")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("👋🏽")]
    public void KeepsANameThatIsSimplyAName(string raw)
    {
        Assert.True(PersonName.TryNormalize(raw, out var name, out _));
        Assert.Equal(raw, name);
    }

    [Theory]
    [InlineData("  Adam  ", "Adam")]
    [InlineData("Ada    Lovelace", "Ada Lovelace")]
    [InlineData("Ada\tLovelace", "Ada Lovelace")]
    // A no-break space is still a space to a reader, so it collapses like one.
    [InlineData("Ada\u00A0Lovelace", "Ada Lovelace")]
    // A newline is a separator, not a deletion - so it collapses to a space
    // like every other kind of whitespace. What matters is that the stored name
    // is one line, because a name with a line break in it breaks every log line
    // that carries it and every table row that renders it.
    [InlineData("Adam\nRevoked everything", "Adam Revoked everything")]
    public void TreatsWhitespaceAsSeparationRatherThanLayout(string raw, string expected)
    {
        Assert.True(PersonName.TryNormalize(raw, out var name, out _));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void ComposesTheTwoSpellingsOfTheSameNameIntoOne()
    {
        // "Jose" written two ways - e plus a combining acute, and one
        // precomposed e-acute. They render identically, so storing both is how
        // a household ends up with two people who look like one, and with a
        // search that finds neither.
        Assert.True(PersonName.TryNormalize("Jose\u0301", out var decomposed, out _));
        Assert.True(PersonName.TryNormalize("José", out var composed, out _));

        Assert.Equal(composed, decomposed);
        Assert.Equal(4, PersonName.CountGraphemes(decomposed));
    }

    [Theory]
    // U+202E, the Trojan Source character. Left in, it reverses everything
    // rendered after it, so a name could rearrange the log line or the table
    // row it appears inside.
    [InlineData("Adam\u202E", "Adam")]
    [InlineData("\u200BAdam", "Adam")]
    [InlineData("Ad\u00ADam", "Adam")]
    public void StripsTheCharactersThatAreInstructionsRatherThanLetters(string raw, string expected)
    {
        Assert.True(PersonName.TryNormalize(raw, out var name, out _));
        Assert.Equal(expected, name);
    }

    [Fact]
    public void KeepsTheOneInvisibleCharacterThatIsLoadBearing()
    {
        // A zero-width joiner is the same Unicode category as the bidi override
        // above, and dropping it with the rest would turn one family into four
        // separate people standing in a row.
        Assert.True(PersonName.TryNormalize("👨‍👩‍👧‍👦", out var name, out _));

        Assert.Equal(1, PersonName.CountGraphemes(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Not blank on the way in, and nothing at all on the way out - the case a
    // naive IsNullOrWhiteSpace check at the top would let through into an
    // empty row.
    [InlineData("\u202E\u200B")]
    public void RefusesANameThatIsNotThere(string? raw)
    {
        Assert.False(PersonName.TryNormalize(raw, out _, out var error));
        Assert.Equal(PersonName.EmptyError, error);
    }

    [Fact]
    public void MeasuresLengthTheWayAReaderWould()
    {
        // Sixty families: one grapheme each to a person, eleven chars each to
        // .NET. Counting code units would refuse this at the fourth name.
        var families = string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", PersonName.MaxGraphemes));

        Assert.Equal(PersonName.MaxGraphemes, PersonName.CountGraphemes(families));

        // Refused - but by the column's own bound, not by the reader's rule.
        // This is the pathological case MaxChars exists for, and reaching it
        // takes deliberate effort.
        Assert.True(families.Length > PersonName.MaxChars);
        Assert.False(PersonName.TryNormalize(families, out _, out var error));
        Assert.Equal(PersonName.TooLongError, error);
    }

    [Fact]
    public void AcceptsExactlyTheStatedLengthAndNotOneMore()
    {
        var atLimit = new string('a', PersonName.MaxGraphemes);

        Assert.True(PersonName.TryNormalize(atLimit, out var name, out _));
        Assert.Equal(atLimit, name);

        Assert.False(PersonName.TryNormalize(atLimit + "a", out _, out var error));
        Assert.Equal(PersonName.TooLongError, error);
    }

    [Fact]
    public void MeasuresAfterTrimmingRatherThanBefore()
    {
        // A name that fits, typed by someone who leaned on the space bar. A
        // refusal here would be unexplainable to whoever is standing at the
        // form, because the thing they can see is exactly at the limit.
        var padded = "   " + new string('a', PersonName.MaxGraphemes) + "   ";

        Assert.True(PersonName.TryNormalize(padded, out var name, out _));
        Assert.Equal(PersonName.MaxGraphemes, name.Length);
    }

    [Fact]
    public void LeavesAloneTheQuotesAndBracketsAnEscapingPassWouldMangle()
    {
        // Deliberately not escaped here: EF parameterizes the write and React
        // escapes the render. A name stored pre-escaped is a name that grows
        // ampersands every time someone opens the edit form.
        const string awkward = "Bob <b>&</b> \"Alice\"; DROP TABLE People--";

        Assert.True(PersonName.TryNormalize(awkward, out var name, out _));
        Assert.Equal(awkward, name);
    }
}
