using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Photos;

/// <summary>One photo the wall may show, with the album it came from already resolved - the carousel captions with the album's name, not its id.</summary>
public record LibraryPhoto(string AssetId, string AlbumName, DateTimeOffset? TakenAt, string? City, string? Country);

/// <summary>
/// Everything the included albums currently hold.
/// </summary>
/// <param name="AssetIds">
/// The same photos as a set, which is what makes the image endpoint an
/// allow-list rather than an open proxy: an asset id that is not in here is not
/// in an included album, and Aerie will not fetch it. Without this the kiosk -
/// or anything that reached the kiosk's origin - could walk the whole library
/// by guessing ids.
/// </param>
/// <param name="Error">Why the last rebuild failed, if it did. Photos is then whatever the previous good fetch held.</param>
public record PhotoLibrarySnapshot(
    IReadOnlyList<LibraryPhoto> Photos,
    IReadOnlySet<string> AssetIds,
    DateTimeOffset BuiltAt,
    string? Error)
{
    public static readonly PhotoLibrarySnapshot Empty =
        new([], new HashSet<string>(), DateTimeOffset.MinValue, null);
}

public interface IPhotoLibrary
{
    /// <summary>The current library, rebuilt from Immich if the cache is stale or the selection changed. Never throws.</summary>
    Task<PhotoLibrarySnapshot> GetAsync(CancellationToken ct);

    /// <summary>Drops the cache so the next read refetches. Called when the selection is written, so a toggled album shows up on the wall now rather than in a quarter of an hour.</summary>
    void Invalidate();
}

/// <summary>
/// The included albums' photos, held in memory rather than in a table.
///
/// This is the module's one interesting decision. The alternative - a row per
/// asset, kept current by a job - would be a second copy of a library that
/// already has a database, and every bug in it would be a divergence between
/// two sources of truth about the same photo. What the wall actually needs is
/// "a few dozen asset ids I may show", which is one Immich call per included
/// album and fits in memory at any family-library size.
///
/// Cached in two layers, for two different kinds of staleness:
///
///   - The <em>selection</em> is re-read from Postgres at most every
///     <see cref="SelectionRecheckInterval"/>. That is what carries another
///     replica's toggle across, since Invalidate only reaches this process.
///   - The <em>photos</em> are refetched from Immich every <see cref="CacheTtl"/>,
///     or immediately when the selection turns out to have changed.
///
/// A failed rebuild keeps the previous snapshot and retries sooner. A photo
/// frame showing a fifteen-minute-old set of photos is not a bug; a photo frame
/// going black because Immich restarted is.
/// </summary>
public class PhotoLibrary(IServiceScopeFactory scopes, TimeProvider time, ILogger<PhotoLibrary> logger) : IPhotoLibrary
{
    /// <summary>How long a good fetch is trusted. A family album gains photos in an afternoon, not in a second.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

    /// <summary>How long a failed fetch is left alone before trying again - short enough that a restarted Immich is picked up while someone is still watching, long enough not to hammer a dead one.</summary>
    public static readonly TimeSpan FailureRetryAfter = TimeSpan.FromMinutes(1);

    /// <summary>How often the selection itself is re-read. Cheap (one indexed read of a table with tens of rows) but not free, and the image endpoint asks per photo.</summary>
    public static readonly TimeSpan SelectionRecheckInterval = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim gate = new(1, 1);

    private PhotoLibrarySnapshot? cached;
    private string? cachedSelection;
    private DateTimeOffset expiresAt;
    private DateTimeOffset selectionCheckedAt;

    public async Task<PhotoLibrarySnapshot> GetAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (cached is { } fresh && now < expiresAt && now - selectionCheckedAt < SelectionRecheckInterval) return fresh;

        await gate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow();
            var stillFresh = cached is not null && now < expiresAt;
            if (stillFresh && now - selectionCheckedAt < SelectionRecheckInterval) return cached!;

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PhotosContext>();

            var albums = await db.Albums.AsNoTracking()
                .Where(a => a.Included)
                .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
                .Select(a => new { a.ImmichAlbumId, a.Name })
                .ToListAsync(ct);

            selectionCheckedAt = now;

            // The selection as a string, so "which albums, called what" is one
            // comparison. The name is in it because it is the caption: an album
            // renamed in Immich should re-caption without waiting out the TTL.
            var selection = string.Join('|', albums.Select(a => $"{a.ImmichAlbumId}:{a.Name}"));
            if (stillFresh && selection == cachedSelection) return cached!;

            var client = scope.ServiceProvider.GetRequiredService<IImmichClient>();
            var photos = new List<LibraryPhoto>();
            string? error = null;

            foreach (var album in albums)
            {
                var result = await client.ListAlbumPhotosAsync(album.ImmichAlbumId, ct);
                if (!result.Succeeded)
                {
                    // One unreachable album does not discard the others: a wall
                    // showing three albums out of four beats a wall showing
                    // none because the fourth was deleted upstream.
                    error ??= result.Error;
                    logger.LogWarning("Could not read Immich album {Album}: {Error}", album.ImmichAlbumId, result.Error);
                    continue;
                }

                photos.AddRange(result.Items.Select(a => new LibraryPhoto(a.Id, album.Name, a.TakenAt, a.City, a.Country)));
            }

            // A total failure keeps what was there. A partial one does not:
            // some albums answered, and what they returned is current.
            if (error is not null && photos.Count == 0 && cached is { Photos.Count: > 0 } previous)
            {
                expiresAt = time.GetUtcNow() + FailureRetryAfter;
                cachedSelection = null;
                return cached = previous with { Error = error };
            }

            var snapshot = new PhotoLibrarySnapshot(
                photos,
                photos.Select(p => p.AssetId).ToHashSet(StringComparer.Ordinal),
                time.GetUtcNow(),
                error);

            cached = snapshot;
            // Only a clean fetch is allowed to claim it covers this selection.
            // After a partial one the next read past the retry window rebuilds,
            // rather than treating the gap as settled.
            cachedSelection = error is null ? selection : null;
            expiresAt = time.GetUtcNow() + (error is null ? CacheTtl : FailureRetryAfter);

            logger.LogInformation("Photo library rebuilt: {Photos} photos from {Albums} albums{Partial}",
                photos.Count, albums.Count, error is null ? "" : $" (partial: {error})");

            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate()
    {
        // Expiring rather than nulling: a concurrent reader gets the old
        // snapshot for the moment before the rebuild lands, which is a better
        // answer than an empty one.
        expiresAt = DateTimeOffset.MinValue;
        selectionCheckedAt = DateTimeOffset.MinValue;
        cachedSelection = null;
    }
}
