using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Photos;

/// <summary>
/// One album as Immich reports it, plus the one fact Hatch owns about it:
/// whether the wall may show it.
///
/// The division of ownership is the same one CalendarDiscoveryService keeps,
/// and for the same reason. Immich owns what an album *is* - its name, its
/// cover, how many assets are in it - and a refresh overwrites all of that.
/// Hatch owns <see cref="Included"/> and <see cref="SortOrder"/>, which a
/// refresh never touches, so "Refresh albums" cannot silently take a wedding
/// album off the kitchen wall.
///
/// The photos themselves are deliberately *not* here. A row per asset would be
/// a second copy of a library that already has a database, kept in sync by a
/// job, to answer a question ("what may the kiosk show next") that a cache
/// answers in one call - see <see cref="IPhotoLibrary"/>.
/// </summary>
[Table("Albums")]
[Index(nameof(ImmichAlbumId), IsUnique = true)]
public class PhotoAlbum
{
    /// <summary>Immich's own limit on an album name, mirrored so a rename upstream can always be stored.</summary>
    public const int MaxNameLength = 200;

    public const int MaxDescriptionLength = 1000;

    /// <summary>Immich ids are UUIDs today; sized for an id rather than pinned to that shape, since it is their string, not ours.</summary>
    public const int MaxProviderIdLength = 64;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>The album's id in Immich - the natural key a refresh matches on, and what the carousel asks Immich for.</summary>
    [MaxLength(MaxProviderIdLength)]
    public required string ImmichAlbumId { get; set; }

    [MaxLength(MaxNameLength)]
    public required string Name { get; set; }

    [MaxLength(MaxDescriptionLength)]
    public string? Description { get; set; }

    /// <summary>How many assets Immich says the album holds. Shown in the admin list so an operator can tell a real album from an empty one before including it.</summary>
    public int AssetCount { get; set; }

    /// <summary>The asset Immich uses as the album's cover, proxied by the admin page's thumbnail endpoint. Null for an empty album.</summary>
    [MaxLength(MaxProviderIdLength)]
    public string? ThumbnailAssetId { get; set; }

    /// <summary>
    /// Whether the kiosk carousel draws from this album. False on discovery, on
    /// purpose and for the reason EfCalendar.Included is: connecting a library
    /// must not put every photo in it on a wall in the kitchen. The operator
    /// opts each album in.
    /// </summary>
    public bool Included { get; set; }

    /// <summary>Ascending display order in the admin list. The carousel shuffles, so this orders the choosing, not the showing.</summary>
    public int SortOrder { get; set; }

    /// <summary>When Immich last recorded a change to the album. Informational - it is what tells an operator whether "0 assets" is stale.</summary>
    public DateTimeOffset? ProviderUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When Hatch last wrote this row, from either half - a refresh or an operator's toggle.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
