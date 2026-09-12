using Hatch.Api.Modules.Gather;

namespace Hatch.Api.Tests.Gather;

/// <summary>
/// Covers name normalization - the rule that decides when two things someone
/// typed are the same item. It is the whole of the duplicate story: the unique
/// index is over its output, so anything it folds together becomes one row and
/// anything it keeps apart becomes two.
/// </summary>
public class GatherItemTests
{
    [Theory]
    [InlineData("Milk", "milk")]
    [InlineData("MILK", "milk")]
    [InlineData("  milk  ", "milk")]
    [InlineData("Paper   Towels", "paper towels")]
    [InlineData("Paper\tTowels", "paper towels")]
    public void Normalize_FoldsCaseAndSpacing_BecauseThoseAreTheSameItem(string typed, string expected)
        => Assert.Equal(expected, GatherItem.Normalize(typed));

    /// <summary>
    /// Deliberately not a stemmer. Being wrong costs differently in each
    /// direction: a stray "apples" next to "apple" is a line someone crosses
    /// off, while folding them would quietly drop one of the two quantities.
    /// </summary>
    [Fact]
    public void Normalize_LeavesATrailingPluralAlone()
        => Assert.NotEqual(GatherItem.Normalize("apple"), GatherItem.Normalize("apples"));

    [Theory]
    [InlineData("2% Milk", "2% milk")]
    [InlineData("Ben & Jerry's", "ben & jerry's")]
    public void Normalize_KeepsPunctuation_BecauseBrandNamesAreFullOfIt(string typed, string expected)
        => Assert.Equal(expected, GatherItem.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_IsEmpty_WhenThereIsNoName(string? typed)
        => Assert.Equal("", GatherItem.Normalize(typed));

    /// <summary>
    /// Setting the display name recomputes the normalized one, so no write path
    /// can leave the two disagreeing and slip a duplicate past the index.
    /// </summary>
    [Fact]
    public void Name_KeepsNameNormalizedInStep()
    {
        var item = new GatherItem { Name = "  Whole  Milk " };
        Assert.Equal("Whole  Milk", item.Name);
        Assert.Equal("whole milk", item.NameNormalized);

        item.Name = "Oat Milk";
        Assert.Equal("oat milk", item.NameNormalized);
    }
}
