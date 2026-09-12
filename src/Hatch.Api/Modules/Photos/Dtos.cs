namespace Hatch.Api.Modules.Photos;

// The API-facing shapes for Photos. Two audiences, deliberately different:
// the admin page reads albums, the kiosk reads photos, and neither is ever
// handed an Immich URL or an Immich key - every byte comes back through this
// API (docs/plans/immich.md v+2).

/// <summary>
/// One album on the admin page. Carries no cover URL: the page builds it from
/// <see cref="Id"/> against the module's own cover endpoint, so the Immich host
/// stays a server-side fact.
/// </summary>
public record PhotoAlbumDto(
    Guid Id,
    string ImmichAlbumId,
    string Name,
    string? Description,
    int AssetCount,
    bool HasCover,
    bool Included,
    int SortOrder,
    DateTimeOffset? ProviderUpdatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The admin-owned half of an album, and the only half that is writable.
/// SortOrder is optional because the common write is a checkbox: omitting it
/// leaves the arrangement alone rather than resetting it to zero.
/// </summary>
public record PhotoAlbumSelectionRequest(bool Included, int? SortOrder);

/// <summary>What one refresh changed. The counts are for the log line and the page's toast.</summary>
public record PhotoAlbumSyncDto(int Added, int Updated, int Removed);

/// <summary>
/// Whether Photos is set up and working, in the terms the admin page needs to
/// decide what to draw: a form, an error, or a list of albums.
/// </summary>
/// <param name="IsConfigured">Both a host and a key are set. Says nothing about whether either is right.</param>
/// <param name="BaseUrl">The configured host, echoed back so the page can show what it is talking to. Never the key.</param>
/// <param name="Reachable">Immich answered and accepted the key.</param>
/// <param name="Error">Why not, when it did not - see ImmichClient's named codes.</param>
public record PhotosStatusDto(
    bool IsConfigured,
    string? BaseUrl,
    bool HasApiKey,
    bool Reachable,
    string? Version,
    string? Error,
    int AlbumCount,
    int IncludedAlbumCount);

/// <summary>
/// One photo in the carousel. The caption fields are all optional because
/// Immich's knowledge of them is: a scanned print has no city and no date, and
/// the wall shows what there is.
/// </summary>
public record CarouselPhotoDto(string AssetId, string AlbumName, DateTimeOffset? TakenAt, string? City, string? Country);

/// <summary>
/// A shuffled slice of everything the included albums hold - the wall's whole
/// contract with this module.
/// </summary>
/// <param name="TotalPhotos">How many photos the selection holds in all, of which Photos is a sample. What tells an admin that including one more album did something.</param>
/// <param name="Error">Set when the library could not be refreshed. Photos may still be populated from the last good fetch, which is the intended degradation.</param>
public record PhotoCarouselDto(
    IReadOnlyList<CarouselPhotoDto> Photos,
    int TotalPhotos,
    DateTimeOffset GeneratedAt,
    string? Error);
