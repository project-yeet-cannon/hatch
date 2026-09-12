using Hatch.Api.Modules.Storage;

namespace Hatch.Api.Tests.Storage;

/// <summary>
/// Covers the translation from a search box to a Postgres <c>tsquery</c> - the
/// half of search that isn't the database's. What the query then <em>finds</em>
/// (stemming, prefixes, matching across crate and location) is Postgres's own
/// behaviour and is verified against a real one, not here: EF's InMemory provider
/// has no tsvector at all.
/// </summary>
public class SearchQueryTests
{
    [Fact]
    public void ToTsQuery_MakesEveryTermAPrefix_SoResultsNarrowWhileTyping()
        => Assert.Equal("dri:*", SearchQuery.ToTsQuery("dri"));

    [Fact]
    public void ToTsQuery_RequiresEveryTerm_SoTheThingAndThePlaceMatchTogether()
        => Assert.Equal("drill:* & garage:*", SearchQuery.ToTsQuery("drill garage"));

    [Theory]
    [InlineData("Drill Bits", "drill:* & bits:*")]
    [InlineData("drill,bits", "drill:* & bits:*")]
    [InlineData("  drill   bits  ", "drill:* & bits:*")]
    [InlineData("HZ4-YHB", "hz4:* & yhb:*")]
    public void ToTsQuery_CutsInputIntoTerms_OnAnythingThatIsNotALetterOrDigit(string input, string expected)
        => Assert.Equal(expected, SearchQuery.ToTsQuery(input));

    /// <summary>
    /// The reason terms are letters and digits only: no tsquery operator can reach
    /// the parser from typed text, so a stray quote or ampersand is a word break
    /// rather than a syntax error thrown at someone mid-search.
    /// </summary>
    [Fact]
    public void ToTsQuery_DropsTsQueryOperators_RatherThanPassingThemThrough()
        => Assert.Equal("drill:* & bits:* & x:*", SearchQuery.ToTsQuery("drill & !(bits:*) | 'x"));

    [Fact]
    public void ToTsQuery_DropsRepeatedTerms()
        => Assert.Equal("drill:*", SearchQuery.ToTsQuery("drill DRILL drill"));

    [Fact]
    public void ToTsQuery_KeepsAtMostMaxTerms()
    {
        var query = SearchQuery.ToTsQuery(string.Join(' ', Enumerable.Range(0, SearchQuery.MaxTerms + 5).Select(i => $"t{i}")));

        Assert.Equal(SearchQuery.MaxTerms, query!.Split(" & ").Length);
    }

    [Fact]
    public void ToTsQuery_IgnoresInputPastTheLengthCap()
    {
        var query = SearchQuery.ToTsQuery(new string('a', SearchQuery.MaxInputLength) + " drill");

        Assert.DoesNotContain("drill", query);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("  !!  ")]
    public void ToTsQuery_IsNull_WhenThereIsNothingToSearchFor(string? input)
        => Assert.Null(SearchQuery.ToTsQuery(input));
}
