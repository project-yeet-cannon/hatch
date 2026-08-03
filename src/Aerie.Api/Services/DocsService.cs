using System.Text.RegularExpressions;
using Aerie.Api.Models.Docs;

namespace Aerie.Api.Services;

public interface IDocsService
{
    /// <summary>All markdown docs under the configured Docs:Path, sorted by title.</summary>
    IReadOnlyList<DocSummary> GetAll();

    /// <summary>Raw markdown for the given slug, or null if it doesn't match a known doc.</summary>
    Task<string?> GetContentAsync(string slug, CancellationToken ct = default);
}

/// <summary>
/// Backs the docs browser app (apps/docs): lists and serves the markdown files
/// under /docs (see Docs:Path in appsettings.*.json) so the SPA doesn't need
/// its own bundled/stale copy of them.
/// </summary>
public class DocsService(IConfiguration configuration, IWebHostEnvironment env) : IDocsService
{
    private static readonly Regex TitleHeading = new(@"^#\s+(.*)$", RegexOptions.Compiled);

    private string DocsRoot => Path.GetFullPath(Path.Combine(env.ContentRootPath, configuration["Docs:Path"] ?? "docs"));

    public IReadOnlyList<DocSummary> GetAll()
    {
        if (!Directory.Exists(DocsRoot))
            return [];

        return Directory.EnumerateFiles(DocsRoot, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => new DocSummary(Path.GetFileNameWithoutExtension(path), TitleFor(path)))
            .OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string?> GetContentAsync(string slug, CancellationToken ct = default)
    {
        // GetAll() (not raw disk access) is the whitelist here, so a slug like
        // "../../appsettings" can't escape DocsRoot.
        if (!GetAll().Any(d => d.Slug == slug))
            return null;

        return await File.ReadAllTextAsync(Path.Combine(DocsRoot, slug + ".md"), ct);
    }

    private static string TitleFor(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var match = TitleHeading.Match(line);
            if (match.Success)
                return match.Groups[1].Value.Trim();
        }

        return Path.GetFileNameWithoutExtension(path);
    }
}
