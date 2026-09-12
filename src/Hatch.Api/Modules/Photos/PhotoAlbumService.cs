using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Photos;

/// <summary>
/// What one refresh changed, or why it changed nothing. The counts are for the
/// log line and the admin page's toast; Error is what a caller branches on.
/// </summary>
public record PhotoAlbumSyncResult(int Added, int Updated, int Removed, string? Error)
{
    public bool Succeeded => Error is null;

    public static PhotoAlbumSyncResult Failed(string error) => new(0, 0, 0, error);
}

public interface IPhotoAlbumService
{
    /// <summary>
    /// Brings PhotoAlbum rows in line with what Immich currently holds. Never
    /// throws: a failure comes back on the result, the same fail-soft contract
    /// ImmichClient keeps.
    /// </summary>
    Task<PhotoAlbumSyncResult> SyncAlbumsAsync(CancellationToken ct);
}

/// <summary>
/// Reconciles the album list against Immich's.
///
/// The division of ownership is CalendarDiscoveryService's, deliberately: Immich
/// owns what an album is (name, description, cover, count) and a refresh
/// overwrites all of it; Hatch owns Included and SortOrder and a refresh never
/// touches either. Otherwise "Refresh albums" would quietly un-choose what
/// somebody chose.
/// </summary>
public class PhotoAlbumService(
    PhotosContext db,
    IImmichClient immich,
    IPhotoLibrary library,
    TimeProvider time,
    ILogger<PhotoAlbumService> logger) : IPhotoAlbumService
{
    public async Task<PhotoAlbumSyncResult> SyncAlbumsAsync(CancellationToken ct)
    {
        var fetched = await immich.ListAlbumsAsync(ct);
        if (!fetched.Succeeded)
        {
            // Nothing is deleted here, and that is the point. A failed fetch is
            // an empty list on the wire, and reading it as the truth would wipe
            // every album - taking the Included flags with it - the first time
            // the photo server was down.
            logger.LogWarning("Could not list Immich albums: {Error}", fetched.Error);
            return PhotoAlbumSyncResult.Failed(fetched.Error!);
        }

        var now = time.GetUtcNow();
        var existing = await db.Albums.ToDictionaryAsync(a => a.ImmichAlbumId, ct);
        // A new album lands after everything already arranged rather than at 0,
        // where it would jump to the top of a list somebody ordered.
        var nextSortOrder = existing.Count == 0 ? 0 : existing.Values.Max(a => a.SortOrder) + 1;
        int added = 0, updated = 0;

        foreach (var incoming in fetched.Items)
        {
            if (existing.Remove(incoming.Id, out var album))
            {
                if (Apply(album, incoming))
                {
                    album.UpdatedAt = now;
                    updated++;
                }
            }
            else
            {
                var fresh = new PhotoAlbum
                {
                    ImmichAlbumId = incoming.Id,
                    Name = Truncate(incoming.Name, PhotoAlbum.MaxNameLength)!,
                    // Included stays false: connecting a library must not put
                    // every photo in it on the kitchen wall. The operator
                    // opts each album in.
                    SortOrder = nextSortOrder++,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                Apply(fresh, incoming);
                db.Albums.Add(fresh);
                added++;
            }
        }

        // Whatever is left matched nothing Immich returned - deleted there, or
        // shared with this key and then unshared.
        var removed = existing.Values.ToList();
        if (removed.Count > 0) db.Albums.RemoveRange(removed);

        await db.SaveChangesAsync(ct);

        // A removed album that was included, or a rename that changes a
        // caption, both make the cached library wrong the instant this commits.
        if (added > 0 || updated > 0 || removed.Count > 0) library.Invalidate();

        logger.LogInformation("Immich album refresh: {Added} added, {Updated} updated, {Removed} removed",
            added, updated, removed.Count);

        return new PhotoAlbumSyncResult(added, updated, removed.Count, null);
    }

    /// <summary>Copies the provider-owned half onto the row, reporting whether anything actually differed - so an unchanged album isn't counted as updated on every refresh.</summary>
    private static bool Apply(PhotoAlbum album, ImmichAlbum incoming)
    {
        // Non-null by ImmichClient's contract: a nameless album comes back named for its id.
        var name = Truncate(incoming.Name, PhotoAlbum.MaxNameLength)!;
        var description = Truncate(incoming.Description, PhotoAlbum.MaxDescriptionLength);

        var changed = album.Name != name
            || album.Description != description
            || album.AssetCount != incoming.AssetCount
            || album.ThumbnailAssetId != incoming.ThumbnailAssetId
            || album.ProviderUpdatedAt != incoming.UpdatedAt;

        album.Name = name;
        album.Description = description;
        album.AssetCount = incoming.AssetCount;
        album.ThumbnailAssetId = incoming.ThumbnailAssetId;
        album.ProviderUpdatedAt = incoming.UpdatedAt;

        return changed;
    }

    /// <summary>
    /// Immich's limits are its own, so a name longer than the column is a
    /// possibility rather than an impossibility. Truncating beats failing the
    /// whole refresh over one album with an essay for a title. Blank comes back
    /// null, since an empty description is an absent one.
    /// </summary>
    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Length <= max ? value
        : value[..max];
}
