using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Modules.Photos;

/// <summary>One album as Immich describes it, with Immich's spelling already normalized away - PhotoAlbumService writes these into PhotoAlbum rows without knowing where they came from.</summary>
public record ImmichAlbum(string Id, string Name, string? Description, int AssetCount, string? ThumbnailAssetId, DateTimeOffset? UpdatedAt);

/// <summary>
/// One photo in an album. Everything past the id is caption material for the
/// carousel: where and when, if Immich knows, and nothing else - a wall does
/// not need a lens or an f-stop.
/// </summary>
public record ImmichAsset(string Id, DateTimeOffset? TakenAt, string? City, string? Country);

/// <summary>What a reachable, correctly-keyed Immich answers with. Version is what the admin page shows to prove the connection is real rather than merely un-refused.</summary>
public record ImmichServer(string? Version);

/// <summary>Rendered image bytes, buffered rather than streamed - see <see cref="ImmichClient.MaxImageBytes"/>.</summary>
public record ImmichImage(byte[] Bytes, string ContentType);

/// <summary>
/// A single-value fetch's outcome. Same shape and same reasoning as
/// ProviderListResult next door in Services/Calendar: a failure is a value the
/// caller reads, never an exception thrown through it.
/// </summary>
public record ImmichResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;

    public static ImmichResult<T> Ok(T value) => new(value, null);

    public static ImmichResult<T> Failed(string error) => new(default, error);
}

/// <summary>A list fetch's outcome. Kept apart from an empty list for the reason discovery needs: "the library has no albums" and "the library could not be reached" must not delete the same rows.</summary>
public record ImmichListResult<T>(IReadOnlyList<T> Items, string? Error)
{
    public bool Succeeded => Error is null;

    public static ImmichListResult<T> Ok(IReadOnlyList<T> items) => new(items, null);

    public static ImmichListResult<T> Failed(string error) => new([], error);
}

public interface IImmichClient
{
    /// <summary>Whether the configured host answers and accepts the configured key, and what version it is. The admin page's "Test connection".</summary>
    Task<ImmichResult<ImmichServer>> GetServerAsync(CancellationToken ct);

    /// <summary>Every album the API key's owner can see, covers and counts included, without any of their assets.</summary>
    Task<ImmichListResult<ImmichAlbum>> ListAlbumsAsync(CancellationToken ct);

    /// <summary>The photos in one album. Videos are dropped here rather than downstream: a still frame is not what a photo frame is for.</summary>
    Task<ImmichListResult<ImmichAsset>> ListAlbumPhotosAsync(string albumId, CancellationToken ct);

    /// <summary>One asset's rendered image at <paramref name="size"/> - see <see cref="ImmichImageSizes"/>. Never the original: see the note there.</summary>
    Task<ImmichResult<ImmichImage>> GetImageAsync(string assetId, string size, CancellationToken ct);
}

/// <summary>
/// The two renditions Immich generates for every asset, and the only two Aerie
/// ever asks for. The original is deliberately not reachable through this
/// module: a 40 MB raw file has no business travelling to a tablet, and an
/// endpoint that can serve one is an endpoint that can exfiltrate the library a
/// photo at a time.
/// </summary>
public static class ImmichImageSizes
{
    /// <summary>Immich's large rendition (~1440px). What the carousel shows.</summary>
    public const string Preview = "preview";

    /// <summary>Immich's small rendition (~250px). What the admin album list shows as a cover.</summary>
    public const string Thumbnail = "thumbnail";

    public static bool IsKnown(string? size) => size is Preview or Thumbnail;
}

/// <summary>
/// Immich's REST API, as much of it as a photo frame needs: list albums, list
/// an album's photos, fetch a rendition.
///
/// Fail-soft like every other outbound client in this app (GoogleCalendarClient,
/// NwsAlertProvider): every path returns a result, so a photo server that is
/// down or re-keyed leaves the carousel showing what it last had rather than
/// throwing out of a dashboard request.
///
/// The host and the key are read from SiteSettings on each call rather than
/// captured, because both are editable from the admin page while the app runs -
/// the same reason HomeAssistantConnectionManager re-reads its three.
/// </summary>
public class ImmichClient(
    IHttpClientFactory httpClientFactory,
    ISiteSettingsService siteSettings,
    ILogger<ImmichClient> logger) : IImmichClient
{
    /// <summary>Named for its role rather than its vendor, matching the convention in Program.cs.</summary>
    public const string HttpClientName = "PhotoLibrary";

    /// <summary>Immich authenticates a machine caller with a header, not a bearer token - so this is not an Authorization header and cannot be sent as one.</summary>
    public const string ApiKeyHeader = "x-api-key";

    /// <summary>Nothing has been set up yet: no host, or no key. A state the admin page renders as a form rather than as an error.</summary>
    public const string NotConfigured = "not_configured";

    /// <summary>The key was refused - revoked in Immich, or pasted wrong.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>No such album or asset. A carousel entry can hit this legitimately: someone deleted the photo since the library was cached.</summary>
    public const string NotFound = "not_found";

    /// <summary>Nothing answered. DNS, a dead pod, the tailnet being down - indistinguishable from here and identically handled.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>
    /// A ceiling on a buffered image, not a target. Immich's own preview
    /// rendition is a JPEG a few hundred KB wide, so this is only ever reached
    /// by a misconfiguration - and buffering is what lets the response carry a
    /// Content-Length and be cached, which a proxied stream would not.
    /// </summary>
    public const int MaxImageBytes = 24 * 1024 * 1024;

    /// <summary>
    /// A stop, not a cap: an album with more photos than this still shows, it
    /// just shows this many. Bounds what one album can cost the in-memory
    /// library when someone includes a 60,000-asset "All photos".
    /// </summary>
    public const int MaxPhotosPerAlbum = 2000;

    public async Task<ImmichResult<ImmichServer>> GetServerAsync(CancellationToken ct)
    {
        // /api/server/about rather than /api/server/ping: ping answers without
        // a key, so a "test connection" built on it would go green for a host
        // whose key is wrong - which is the failure most worth catching here.
        var result = await GetJsonAsync<AboutBody>("/api/server/about", ct);
        return result.Succeeded
            ? ImmichResult<ImmichServer>.Ok(new ImmichServer(NullIfEmpty(result.Value?.Version)))
            : ImmichResult<ImmichServer>.Failed(result.Error!);
    }

    public async Task<ImmichListResult<ImmichAlbum>> ListAlbumsAsync(CancellationToken ct)
    {
        var result = await GetJsonAsync<List<AlbumBody>>("/api/albums", ct);
        if (!result.Succeeded) return ImmichListResult<ImmichAlbum>.Failed(result.Error!);

        var albums = (result.Value ?? [])
            .Select(ToAlbum)
            .OfType<ImmichAlbum>()
            .ToList();

        return ImmichListResult<ImmichAlbum>.Ok(albums);
    }

    public async Task<ImmichListResult<ImmichAsset>> ListAlbumPhotosAsync(string albumId, CancellationToken ct)
    {
        var result = await GetJsonAsync<AlbumBody>($"/api/albums/{Uri.EscapeDataString(albumId)}", ct);
        if (!result.Succeeded) return ImmichListResult<ImmichAsset>.Failed(result.Error!);

        var photos = (result.Value?.Assets ?? [])
            // IMAGE only. A video's poster frame is a photo the way a book
            // cover is a book, and a carousel that pauses on one looks broken.
            .Where(a => string.Equals(a.Type, "IMAGE", StringComparison.OrdinalIgnoreCase))
            .Select(ToAsset)
            .OfType<ImmichAsset>()
            .Take(MaxPhotosPerAlbum)
            .ToList();

        return ImmichListResult<ImmichAsset>.Ok(photos);
    }

    public async Task<ImmichResult<ImmichImage>> GetImageAsync(string assetId, string size, CancellationToken ct)
    {
        if (!ImmichImageSizes.IsKnown(size)) return ImmichResult<ImmichImage>.Failed(NotFound);

        var connection = await ConnectionAsync(ct);
        if (connection is null) return ImmichResult<ImmichImage>.Failed(NotConfigured);

        var path = $"/api/assets/{Uri.EscapeDataString(assetId)}/thumbnail?size={size}";

        try
        {
            using var response = await SendAsync(connection, path, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorFor(response.StatusCode);
                logger.LogWarning("Immich refused {Path} with {Status} ({Error})", path, (int)response.StatusCode, error);
                return ImmichResult<ImmichImage>.Failed(error);
            }

            // Checked before reading rather than after: the point of a ceiling
            // is not to have allocated the thing it forbids.
            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                logger.LogWarning("Immich returned {Bytes} bytes for {Path}, over the {Max} ceiling",
                    response.Content.Headers.ContentLength, path, MaxImageBytes);
                return ImmichResult<ImmichImage>.Failed("too_large");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > MaxImageBytes) return ImmichResult<ImmichImage>.Failed("too_large");

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            return ImmichResult<ImmichImage>.Ok(new ImmichImage(bytes, contentType));
        }
        catch (Exception ex) when (Transient(ex, ct))
        {
            logger.LogWarning(ex, "Immich request to {Path} could not be completed", path);
            return ImmichResult<ImmichImage>.Failed(Unreachable);
        }
    }

    /// <summary>The host and key as currently configured, or null when either is missing - which is a state, not a failure.</summary>
    private async Task<ImmichConnection?> ConnectionAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        if (NullIfEmpty(settings.ImmichBaseUrl) is not { } baseUrl) return null;
        if (NullIfEmpty(settings.ImmichApiKey) is not { } apiKey) return null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme is not ("http" or "https")) return null;

        // Normalized once, here: the setting is typed into a form, and
        // "https://photos/" and "https://photos" are the same server.
        return new ImmichConnection(parsed.GetLeftPart(UriPartial.Path).TrimEnd('/'), apiKey);
    }

    private async Task<ImmichResult<T>> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        var connection = await ConnectionAsync(ct);
        if (connection is null) return ImmichResult<T>.Failed(NotConfigured);

        try
        {
            using var response = await SendAsync(connection, path, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorFor(response.StatusCode);
                logger.LogWarning("Immich refused {Path} with {Status} ({Error})", path, (int)response.StatusCode, error);
                return ImmichResult<T>.Failed(error);
            }

            var body = JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(ct));
            return body is null
                ? ImmichResult<T>.Failed("empty_response")
                : ImmichResult<T>.Ok(body);
        }
        catch (Exception ex) when (Transient(ex, ct))
        {
            logger.LogWarning(ex, "Immich request to {Path} could not be completed", path);
            return ImmichResult<T>.Failed(Unreachable);
        }
    }

    private Task<HttpResponseMessage> SendAsync(ImmichConnection connection, string path, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        // Concatenated rather than new Uri(base, path): an absolute path
        // resolved against a base *replaces* its path, so an Immich served
        // under a prefix - https://home.example.com/photos - would have that
        // prefix silently dropped on every call.
        var request = new HttpRequestMessage(HttpMethod.Get, connection.BaseUrl + path);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, connection.ApiKey);
        request.Headers.Accept.ParseAdd("application/json");
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>A caller's own cancellation is not a provider failure, so it is allowed to propagate; everything else on the way to Immich is.</summary>
    private static bool Transient(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested;

    /// <summary>Named codes for the statuses a caller branches on; everything else keeps its number, which is enough to put in front of an admin.</summary>
    private static string ErrorFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Unauthorized,
        HttpStatusCode.NotFound => NotFound,
        _ => $"http_{(int)status}",
    };

    private static ImmichAlbum? ToAlbum(AlbumBody body) =>
        NullIfEmpty(body.Id) is { } id
            ? new ImmichAlbum(
                id,
                // An album with no name is legal in Immich and shows there as
                // blank; its id beats a blank row in a list you have to choose from.
                NullIfEmpty(body.AlbumName) ?? id,
                NullIfEmpty(body.Description),
                // assetCount is absent on some responses, where the assets
                // array is the count.
                body.AssetCount ?? body.Assets?.Count ?? 0,
                NullIfEmpty(body.AlbumThumbnailAssetId),
                ParseTime(body.UpdatedAt))
            : null;

    private static ImmichAsset? ToAsset(AssetBody body) =>
        NullIfEmpty(body.Id) is { } id
            // localDateTime first: it is when the photo was taken where it was
            // taken, which is what a caption means by "when". fileCreatedAt is
            // the fallback for an asset with no EXIF date at all.
            ? new ImmichAsset(id,
                ParseTime(body.LocalDateTime) ?? ParseTime(body.FileCreatedAt),
                NullIfEmpty(body.ExifInfo?.City),
                NullIfEmpty(body.ExifInfo?.Country))
            : null;

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private record ImmichConnection(string BaseUrl, string ApiKey);

    private record AboutBody([property: JsonPropertyName("version")] string? Version);

    private record AlbumBody(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("albumName")] string? AlbumName,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("assetCount")] int? AssetCount,
        [property: JsonPropertyName("albumThumbnailAssetId")] string? AlbumThumbnailAssetId,
        [property: JsonPropertyName("updatedAt")] string? UpdatedAt,
        [property: JsonPropertyName("assets")] List<AssetBody>? Assets);

    private record AssetBody(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("fileCreatedAt")] string? FileCreatedAt,
        [property: JsonPropertyName("localDateTime")] string? LocalDateTime,
        [property: JsonPropertyName("exifInfo")] ExifBody? ExifInfo);

    private record ExifBody(
        [property: JsonPropertyName("city")] string? City,
        [property: JsonPropertyName("country")] string? Country);
}
