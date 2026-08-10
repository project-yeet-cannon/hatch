using System.Text.RegularExpressions;

namespace Aerie.Api.Modules.Storage;

/// <summary>
/// Turns what someone typed into a Postgres <c>tsquery</c> string.
/// </summary>
/// <remarks>
/// The rules are all about the moment this is used: standing in a garage,
/// thumb-typing, halfway through a word.
/// <list type="bullet">
/// <item>Every term is a <b>prefix</b> (<c>drill:*</c>), so results narrow while
/// typing rather than only once a word is finished.</item>
/// <item>Terms are <b>AND</b>ed, so "drill garage" means the drill in the garage -
/// which is how people recall where something is, by the thing and the place.</item>
/// <item>Input is cut into runs of letters and digits and nothing else survives,
/// so no <c>tsquery</c> operator (<c>&amp; | ! ( ) : *</c>) can reach the parser
/// from user text and a stray quote can't turn into a syntax error mid-word.</item>
/// </list>
/// </remarks>
public static partial class SearchQuery
{
    /// <summary>
    /// The text search configuration, which is what buys stemming ("lights"
    /// finds "light") and stop-word removal. English because the house is.
    /// </summary>
    public const string Config = "english";

    /// <summary>
    /// Terms past this are dropped rather than making the query slower and
    /// narrower. Nobody types eight words to find a drill.
    /// </summary>
    public const int MaxTerms = 8;

    /// <summary>Input past this is ignored - a search box is not a document upload.</summary>
    public const int MaxInputLength = 200;

    /// <summary>Most rows a search will return, so a bad query can't hand a phone the whole index.</summary>
    public const int MaxResults = 200;

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TermPattern { get; }

    /// <summary>
    /// The <c>tsquery</c> for <paramref name="input"/>, or null if there's nothing
    /// to search for - which callers should treat as "no query", not "no results",
    /// because an empty search box means the whole list rather than none of it.
    /// </summary>
    public static string? ToTsQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var terms = TermPattern.Matches(input.Length > MaxInputLength ? input[..MaxInputLength] : input)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxTerms)
            .ToList();

        return terms.Count == 0 ? null : string.Join(" & ", terms.Select(t => $"{t}:*"));
    }
}
