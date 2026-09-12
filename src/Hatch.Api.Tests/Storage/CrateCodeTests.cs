using Hatch.Api.Modules.Storage;

namespace Hatch.Api.Tests.Storage;

/// <summary>
/// Covers the label alphabet: what a generated code may contain, and what a
/// person standing in a garage may type and still land on the right crate.
/// </summary>
public class CrateCodeTests
{
    [Fact]
    public void Next_DrawsSixCharacters_FromTheCrockfordAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = CrateCode.Next();
            Assert.Equal(CrateCode.Length, code.Length);
            Assert.All(code, c => Assert.Contains(c, CrateCode.Alphabet));
        }
    }

    [Fact]
    public void Alphabet_ExcludesTheAmbiguousLetters()
    {
        Assert.Equal(32, CrateCode.Alphabet.Length);
        Assert.All("ILOU", c => Assert.DoesNotContain(c, CrateCode.Alphabet));
    }

    [Fact]
    public void Format_SplitsIntoTwoGroups_ForPrinting()
        => Assert.Equal("ABC-123", CrateCode.Format("ABC123"));

    [Theory]
    [InlineData("ABC123")]
    [InlineData("abc123")]
    [InlineData("ABC-123")]
    [InlineData("abc-123")]
    [InlineData(" ABC 123 ")]
    public void Normalize_AcceptsWhatSomeoneMightType(string input)
        => Assert.Equal("ABC123", CrateCode.Normalize(input));

    [Theory]
    [InlineData("IBC123", "1BC123")]  // I read off a faded label
    [InlineData("LBC123", "1BC123")]  // and L, which looks like it
    [InlineData("OBC123", "0BC123")]  // and O, which looks like zero
    public void Normalize_AppliesCrockfordSubstitutions(string typed, string expected)
        => Assert.Equal(expected, CrateCode.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABC12")]      // too short
    [InlineData("ABC1234")]    // too long
    [InlineData("ABC12U")]     // U is not in the alphabet, and is not substituted
    [InlineData("ABC12!")]
    public void Normalize_RejectsWhatCannotBeACode(string? input)
        => Assert.Null(CrateCode.Normalize(input));
}
