using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Storage;

/// <summary>
/// Where crates physically live - "Garage", "Attic", "Basement shelf 3".
/// Deliberately flat: nesting is a guess until someone actually misses it,
/// and a flat list is what a phone screen wants anyway.
/// </summary>
[Table("Locations")]
public class StorageLocation
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<Crate> Crates { get; set; } = [];
}

/// <summary>
/// A physical box with a label taped to it. <see cref="Code"/> is the label:
/// generated server-side at creation and never changed, because it exists on
/// tape in a garage where nothing can be re-issued.
/// </summary>
[Table("Crates")]
[Index(nameof(Code), IsUnique = true)]
public class Crate
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// Six characters of Crockford base32, stored bare and uppercase (no dash) -
    /// the dash in "ABC-123" is presentation only. See <see cref="CrateCode"/>.
    /// </summary>
    [MaxLength(CrateCode.Length)]
    public required string Code { get; set; }

    /// <summary>
    /// Human name - "Christmas decorations". Null until someone fills the crate:
    /// crates are created in batches and labelled before they hold anything.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// SET NULL on delete, not cascade: losing a shelf must not silently delete
    /// the record of everything that was on it. The crates go homeless instead.
    /// </summary>
    public Guid? LocationId { get; set; }
    public StorageLocation? Location { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Item> Items { get; set; } = [];
}

/// <summary>
/// One thing inside a crate. Items only exist in a crate - taking the crate
/// away takes them with it (CASCADE), because an item with no box has no
/// answer to the only question this app asks: where is it?
/// </summary>
[Table("Items")]
public class Item
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid CrateId { get; set; }
    public Crate? Crate { get; set; }

    public required string Name { get; set; }

    public int Quantity { get; set; } = 1;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
