using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Aerie.Api.Models.AppVersion;

namespace Aerie.Api.Services;

public interface IAppVersionService
{
    /// <summary>
    /// The build identity of the named frontend app, or null if there's no such
    /// app under wwwroot/apps (or its index.html references no hashed assets,
    /// which means it isn't a Vite build and has nothing stable to compare on).
    /// </summary>
    AppVersionInfo? GetVersion(string app);
}

/// <summary>
/// Derives a frontend app's build identity from the index.html actually being
/// served, rather than from a build-time stamp injected by CI.
///
/// Vite writes content-hashed asset URLs into each app's index.html at build
/// time (<c>/apps/dashboard/assets/index-BGJobmXl.js</c>), so that file's set
/// of asset filenames changes if and only if the app's own bundle changed.
/// Two properties follow, and both are the point:
///
/// - A backend-only deploy - by far the common case here - leaves every app's
///   version byte-identical, so kiosks don't reload for a change they can't see.
/// - A client can compute the same token from the tags in its own DOM (the
///   script/link elements it was actually loaded from), which is the only
///   self-identification that stays correct while a rolling deploy has replicas
///   on two different images. Anything where the client learns its own version
///   by *asking* a replica can latch onto the wrong answer and never recover.
/// </summary>
public class AppVersionService(IWebHostEnvironment env) : IAppVersionService
{
    // Guards the path built below: the app name reaches this service straight
    // off the route, and Path.Combine happily honours "../" in a segment.
    private static readonly Regex AppName = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    // The shape of a filename a bundler emits directly into assets/.
    private static readonly Regex FlatAssetName = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private sealed record CacheEntry(DateTime IndexLastWriteUtc, AppVersionInfo Info);

    public AppVersionInfo? GetVersion(string app)
    {
        if (!AppName.IsMatch(app)) return null;

        var indexPath = Path.Combine(env.WebRootPath, "apps", app, "index.html");
        // Re-stat per call rather than caching outright: inside a container the
        // file is baked into the image and never moves, but `dotnet run` beside
        // a `vite build` rewrites it under a live server, and a stale answer
        // there is a confusing dev-loop bug for no saved work - a stat is far
        // cheaper than the 5-minute poll that triggers it. FileInfo.Exists
        // rather than checking the timestamp for a sentinel: File.GetLastWrite-
        // TimeUtc reports 1601-01-01 for a missing file, not default(DateTime).
        var index = new FileInfo(indexPath);
        if (!index.Exists) return null;

        if (_cache.TryGetValue(app, out var cached) && cached.IndexLastWriteUtc == index.LastWriteTimeUtc)
            return cached.Info;

        var info = Read(app, indexPath);
        if (info is not null) _cache[app] = new CacheEntry(index.LastWriteTimeUtc, info);
        return info;
    }

    private static AppVersionInfo? Read(string app, string indexPath)
    {
        string html;
        try
        {
            html = File.ReadAllText(indexPath);
        }
        catch (IOException)
        {
            // Racing a redeploy that's rewriting the file; the caller polls again.
            return null;
        }

        var assets = ExtractAssetFileNames(app, html);
        return assets.Count == 0 ? null : new AppVersionInfo(app, string.Join('+', assets), assets);
    }

    /// <summary>
    /// The content-hashed asset filenames <paramref name="html"/> references out
    /// of the app's own assets/ directory, deduplicated and ordered. Scoped to
    /// that directory on purpose: cross-app or third-party references (the
    /// Google Fonts stylesheet in the dashboard's head, say) aren't part of this
    /// app's build identity and must not perturb it.
    ///
    /// This has to agree with versionFromAssetUrls in the dashboard's
    /// lib/appVersion.ts on every input, in both directions - a filename one
    /// side keeps and the other drops reads as permanent drift, which is a
    /// tablet that reloads and reloads and never converges. Hence the two-step
    /// shape rather than one clever pattern: take the whole URL up to its
    /// delimiter, then accept only what the client's URL-based parse would also
    /// yield. A single greedy character-class pattern silently captured
    /// "chunks" out of assets/chunks/deep-ABC123.js, where the client, reading
    /// URL.pathname, captured nothing at all.
    /// </summary>
    public static IReadOnlyList<string> ExtractAssetFileNames(string app, string html)
    {
        var pattern = $"""/apps/{Regex.Escape(app)}/assets/([^"'\s>]+)""";
        return Regex.Matches(html, pattern)
            .Select(m => PathSegment(m.Groups[1].Value))
            // Rejects nested paths (the client's URL.pathname parse won't cross a
            // '/' either) and anything that isn't a plain emitted filename.
            .Where(name => FlatAssetName.IsMatch(name))
            .Distinct(StringComparer.Ordinal)
            // Ordinal sort so server and client agree regardless of the order the
            // tags happen to appear in the document.
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Drops any query string or fragment, mirroring what the client gets from
    /// <c>URL.pathname</c>. Vite emits neither on its own asset URLs; this is
    /// here so that if one ever appears, both sides ignore it identically
    /// instead of disagreeing.
    /// </summary>
    private static string PathSegment(string url)
    {
        var cut = url.AsSpan().IndexOfAny('?', '#');
        return cut < 0 ? url : url[..cut];
    }
}
