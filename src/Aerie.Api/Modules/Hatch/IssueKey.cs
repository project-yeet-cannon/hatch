namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The <c>AER-12</c> in a chat window, a branch name, and a URL - the only
/// identifier for an issue that ever leaves this database.
///
/// It is computed rather than stored (<see cref="EfHatchIssue"/>), so this is
/// the one place that knows how the two halves go together and come apart.
/// </summary>
public static class IssueKey
{
    public static string Format(string projectKey, int number) => $"{projectKey}-{number}";

    /// <summary>
    /// Splits a key into its project and its number, upper-casing the project
    /// half so a key typed by hand resolves.
    /// </summary>
    /// <remarks>
    /// The split is on the <em>last</em> hyphen, not the first. Project keys
    /// cannot contain one today, and relying on that would make the day one
    /// can a day when every old key silently resolves to the wrong project.
    /// </remarks>
    public static bool TryParse(string? key, out string projectKey, out int number)
    {
        projectKey = "";
        number = 0;

        if (string.IsNullOrWhiteSpace(key)) return false;

        var hyphen = key.LastIndexOf('-');
        if (hyphen <= 0 || hyphen == key.Length - 1) return false;

        if (!int.TryParse(key[(hyphen + 1)..], out number) || number <= 0) return false;

        projectKey = key[..hyphen].ToUpperInvariant();
        return true;
    }
}

/// <summary>Queries and names that more than one Hatch controller needs.</summary>
public static class HatchQueries
{
    /// <summary>The issue a display key names, in whatever project owns that prefix.</summary>
    public static IQueryable<EfHatchIssue> WithKey(this IQueryable<EfHatchIssue> issues, string projectKey, int number) =>
        issues.Where(i => i.Project!.Key == projectKey && i.Number == number);
}
