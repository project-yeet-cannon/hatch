using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Photos;

/// <summary>
/// Photos' whole API: the admin half chooses albums, the kiosk half draws them.
///
/// Every route is a proxy. The kiosk is never told where Immich is and never
/// holds a key to it (docs/plans/immich.md v+2) - it asks Aerie for a manifest
/// of asset ids and then for those ids' bytes, both on the origin it is already
/// signed in to. That is what keeps the photo frame from needing a second auth,
/// a second origin, or a CORS story, and it is why the Immich key can stay a
/// server-side secret rather than something baked into a tablet.
///
/// Like the rest of the app these sit behind the house wall
/// (docs/auth-architecture.md), which is a gate rather than permissions: any
/// enrolled device can read this, and any enrolled device can change the
/// selection.
/// </summary>
[ApiController]
[Route("api/photos")]
public class PhotosController(
    PhotosContext db,
    IImmichClient immich,
    IPhotoAlbumService albums,
    IPhotoLibrary library,
    ISiteSettingsService siteSettings,
    TimeProvider time) : ControllerBase
{
    /// <summary>How many photos a caller that doesn't say gets. The kiosk always says - it sizes its own deck from its dwell time - so this is for a hand-typed URL and for anything else that grows a use for the manifest.</summary>
    public const int DefaultCarouselSize = 60;

    /// <summary>A ceiling on one manifest, so a hand-typed count cannot ask the API to serialize a whole library.</summary>
    public const int MaxCarouselSize = 200;

    /// <summary>
    /// How long a browser may keep a rendition. Long, because these bytes are
    /// immutable: an Immich asset id names one photo forever, and the rendition
    /// behind it does not change. That is what stops a tablet re-fetching the
    /// same twenty photos every time it cycles round.
    /// </summary>
    public const int ImageCacheSeconds = 24 * 60 * 60;

    // ---- Admin ----

    /// <summary>
    /// Whether Photos is set up and whether it works, in one call - the admin
    /// page's first request. The live check is against Immich rather than
    /// against the settings, because a host and a key that are merely *present*
    /// tell an operator nothing.
    /// </summary>
    [HttpGet("status")]
    public async Task<PhotosStatusDto> GetStatus(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var hasKey = !string.IsNullOrWhiteSpace(settings.ImmichApiKey);
        var configured = !string.IsNullOrWhiteSpace(settings.ImmichBaseUrl) && hasKey;

        var albumCount = await db.Albums.CountAsync(ct);
        var includedCount = await db.Albums.CountAsync(a => a.Included, ct);

        if (!configured)
            return new PhotosStatusDto(false, settings.ImmichBaseUrl, hasKey, false, null, null, albumCount, includedCount);

        var (reachable, version, error) = await ProbeAsync(ct);
        return new PhotosStatusDto(
            IsConfigured: true,
            BaseUrl: settings.ImmichBaseUrl,
            HasApiKey: true,
            Reachable: reachable,
            Version: version,
            Error: error,
            AlbumCount: albumCount,
            IncludedAlbumCount: includedCount);
    }

    /// <summary>
    /// Whether this key works <em>for what Photos does</em>, which is not the
    /// same question as whether it works.
    ///
    /// The probe starts at /api/server/about because it is cheap and reports a
    /// version. But that endpoint is guarded by Immich's own <c>server.about</c>
    /// permission, and a key scoped to exactly what this module needs -
    /// <c>album.read</c> and <c>asset.view</c> - is refused there while working
    /// perfectly everywhere else. Reporting that as "Immich refused that API
    /// key" would send an operator to re-mint a key that was already correct,
    /// which is the worst kind of wrong answer: a red light over a working
    /// system.
    ///
    /// So a 401 there falls through to the call the wall actually depends on. A
    /// key that can list albums is a key that works, version or no version.
    /// </summary>
    private async Task<(bool Reachable, string? Version, string? Error)> ProbeAsync(CancellationToken ct)
    {
        var server = await immich.GetServerAsync(ct);
        if (server.Succeeded) return (true, server.Value?.Version, null);

        // Anything other than a refusal - a dead host, a wrong address - is the
        // same answer for every endpoint, so there is nothing to learn from
        // asking a second one.
        if (server.Error != ImmichClient.Unauthorized) return (false, null, server.Error);

        var probe = await immich.ListAlbumsAsync(ct);
        return probe.Succeeded ? (true, null, null) : (false, null, probe.Error);
    }

    /// <summary>Every album Aerie knows about, whether or not it is on the wall. Ordered the way the admin arranged them.</summary>
    [HttpGet("albums")]
    public async Task<IReadOnlyList<PhotoAlbumDto>> GetAlbums(CancellationToken ct)
        => await db.Albums.AsNoTracking()
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .Select(a => new PhotoAlbumDto(
                a.Id, a.ImmichAlbumId, a.Name, a.Description, a.AssetCount,
                a.ThumbnailAssetId != null, a.Included, a.SortOrder, a.ProviderUpdatedAt, a.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>
    /// Re-reads the album list from Immich. What the admin presses after making
    /// an album over there - and what the page presses on first load, since an
    /// installation that has never refreshed has nothing to choose from.
    /// </summary>
    [HttpPost("albums/refresh")]
    public async Task<ActionResult<PhotoAlbumSyncDto>> RefreshAlbums(CancellationToken ct)
    {
        var result = await albums.SyncAlbumsAsync(ct);
        if (result.Succeeded) return new PhotoAlbumSyncDto(result.Added, result.Updated, result.Removed);

        // A configuration problem is the caller's to fix and reads as one;
        // anything else happened at the far end of a network call.
        return result.Error == ImmichClient.NotConfigured
            ? BadRequest("Set the Immich host and API key first.")
            : StatusCode(StatusCodes.Status502BadGateway, $"Could not list albums from Immich: {result.Error}");
    }

    /// <summary>Sets the admin-owned half of an album. The Immich-owned half only ever changes through a refresh.</summary>
    [HttpPut("albums/{id:guid}")]
    public async Task<ActionResult<PhotoAlbumDto>> UpdateAlbum(Guid id, PhotoAlbumSelectionRequest request, CancellationToken ct)
    {
        var album = await db.Albums.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (album is null) return NotFound();

        var changed = album.Included != request.Included
            || (request.SortOrder is { } order && album.SortOrder != order);

        album.Included = request.Included;
        if (request.SortOrder is { } sortOrder) album.SortOrder = sortOrder;
        if (changed)
        {
            album.UpdatedAt = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            // So the wall reflects the toggle on its next poll rather than at
            // the next expiry - the admin is usually standing in front of both.
            library.Invalidate();
        }

        return new PhotoAlbumDto(
            album.Id, album.ImmichAlbumId, album.Name, album.Description, album.AssetCount,
            album.ThumbnailAssetId is not null, album.Included, album.SortOrder, album.ProviderUpdatedAt, album.UpdatedAt);
    }

    /// <summary>
    /// An album's cover, for the admin list. Separate from the asset endpoint
    /// below because it answers a different question about permission: a cover
    /// is reachable because the album is one Aerie knows about, whereas an
    /// asset is reachable only because it is in an album someone included.
    /// </summary>
    [HttpGet("albums/{id:guid}/cover")]
    public async Task<IActionResult> GetAlbumCover(Guid id, CancellationToken ct)
    {
        var assetId = await db.Albums.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => a.ThumbnailAssetId)
            .FirstOrDefaultAsync(ct);

        if (assetId is null) return NotFound();

        return await ProxyImageAsync(assetId, ImmichImageSizes.Thumbnail, ct);
    }

    // ---- The wall ----

    /// <summary>
    /// A shuffled sample of the included albums' photos - what the kiosk
    /// carousel cycles through until it asks again.
    ///
    /// Shuffled server-side, per request, so two tablets in two rooms are not
    /// showing the same photo at the same moment, and so a refetch is a new
    /// order rather than the same loop again.
    /// </summary>
    [HttpGet("carousel")]
    public async Task<PhotoCarouselDto> GetCarousel([FromQuery] int? count, CancellationToken ct)
    {
        var snapshot = await library.GetAsync(ct);
        var size = Math.Clamp(count ?? DefaultCarouselSize, 1, MaxCarouselSize);

        var sample = Sample(snapshot.Photos, size)
            .Select(p => new CarouselPhotoDto(p.AssetId, p.AlbumName, p.TakenAt, p.City, p.Country))
            .ToList();

        return new PhotoCarouselDto(sample, snapshot.Photos.Count, time.GetUtcNow(), snapshot.Error);
    }

    /// <summary>
    /// One photo's bytes, proxied from Immich.
    ///
    /// The allow-list is the load-bearing part: only an asset that is in an
    /// album someone included is fetched at all. Without it this is a hole
    /// straight through to every photo in the house for anything that can reach
    /// the origin, and asset ids are guessable in exactly the way that matters -
    /// they leak in album listings, backups and browser history.
    /// </summary>
    [HttpGet("assets/{assetId}/image")]
    public async Task<IActionResult> GetAssetImage(string assetId, [FromQuery] string? size, CancellationToken ct)
    {
        var rendition = size ?? ImmichImageSizes.Preview;
        if (!ImmichImageSizes.IsKnown(rendition)) return BadRequest("size must be preview or thumbnail.");

        var snapshot = await library.GetAsync(ct);
        if (!snapshot.AssetIds.Contains(assetId)) return NotFound();

        return await ProxyImageAsync(assetId, rendition, ct);
    }

    // ---- Shared ----

    private async Task<IActionResult> ProxyImageAsync(string assetId, string size, CancellationToken ct)
    {
        var image = await immich.GetImageAsync(assetId, size, ct);
        if (image.Value is not { } bytes)
        {
            return image.Error switch
            {
                ImmichClient.NotConfigured => NotFound(),
                // A photo deleted in Immich since the library was cached is an
                // ordinary event on a wall that cycles for hours, not an error
                // worth a 502 in a log.
                ImmichClient.NotFound => NotFound(),
                _ => StatusCode(StatusCodes.Status502BadGateway),
            };
        }

        // private, not public: an intermediary caching family photos is exactly
        // the thing this whole path exists to avoid.
        Response.Headers.CacheControl = $"private, max-age={ImageCacheSeconds}, immutable";
        return File(bytes.Bytes, bytes.ContentType);
    }

    /// <summary>
    /// <paramref name="count"/> photos drawn at random without replacement - a
    /// partial Fisher-Yates over a copy, rather than sorting the whole library
    /// by a random key on every request. Sampling the union weights an album by
    /// its size, which is the honest reading of "show us our photos": a year
    /// with four hundred of them should come up more often than a day with six.
    /// </summary>
    private static List<LibraryPhoto> Sample(IReadOnlyList<LibraryPhoto> photos, int count)
    {
        if (photos.Count == 0) return [];

        var pool = photos.ToArray();
        var take = Math.Min(count, pool.Length);

        for (var i = 0; i < take; i++)
        {
            var pick = Random.Shared.Next(i, pool.Length);
            (pool[i], pool[pick]) = (pool[pick], pool[i]);
        }

        return [.. pool.Take(take)];
    }
}
