using Hatch.Api.Ef;

namespace Hatch.Api.Services.Media;

/// <summary>
/// Turns whatever an admin typed into a MediaPlayback control into something a
/// speaker can actually fetch. Pure string work - no DB, no HA client - so it
/// unit-tests directly, same convention as ChannelValueExtractor.
///
/// A library-relative path ("Miles Davis/Kind of Blue/01 So What.flac") becomes
/// an absolute URL under MediaLibraryBaseUrl; an http(s) URL or a
/// media-source:// id is already resolvable and passes through untouched.
/// </summary>
public static class MediaLibraryUrlResolver
{
    private const string MediaSourceScheme = "media-source://";

    /// <summary>Resolves <paramref name="input"/> against <paramref name="baseUrl"/> (the MediaLibraryBaseUrl SiteSetting). Exactly one of Url/Error is non-null; Error is admin-facing text, surfaced as the 400 body.</summary>
    public static (string? Url, string? Error) Resolve(string? input, string? baseUrl)
    {
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value)) return (null, "mediaContentId is required");

        // Already resolvable: HA signs its own URL for a media-source id, and an
        // http(s) URL is whatever the admin already knows works.
        if (value.StartsWith(MediaSourceScheme, StringComparison.OrdinalIgnoreCase)) return (value, null);
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return (value, null);

        if (IsLocalOrUncPath(value))
            return (null, $"'{value}' is a local or UNC path, which a speaker can't fetch - it streams over HTTP, not SMB. "
                + "Use a path relative to the media library root instead, e.g. \"Miles Davis/Kind of Blue/01 So What.flac\".");

        if (string.IsNullOrWhiteSpace(baseUrl))
            return (null, $"A library-relative path needs the {SiteSettingKeys.MediaLibraryBaseUrl} site setting, "
                + "set to the base URL speakers should fetch from (e.g. https://home.example.com/media).");

        var segments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return (null, "mediaContentId is required");
        if (segments.Any(s => s is "." or ".."))
            return (null, "A library path can't contain '.' or '..' segments.");

        // Escaped per segment, so spaces, '#' and '&' in real track names survive
        // the trip through HA into the speaker's HTTP client intact.
        var path = string.Join('/', segments.Select(Uri.EscapeDataString));
        return ($"{baseUrl.TrimEnd('/')}/{path}", null);
    }

    /// <summary>"\\NAS\Music\x.mp3", "//NAS/Music/x.mp3" and "D:\Music\x.mp3" - the shapes an admin is most likely to paste out of Explorer, and the ones worth an explanatory error rather than a silent 404 from the speaker.</summary>
    private static bool IsLocalOrUncPath(string value) =>
        value.StartsWith(@"\\", StringComparison.Ordinal)
        || value.StartsWith("//", StringComparison.Ordinal)
        || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');
}
