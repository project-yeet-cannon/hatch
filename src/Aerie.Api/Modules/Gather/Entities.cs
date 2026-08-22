using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Gather;

/// <summary>
/// One named list - "Grocery", "Hardware". Deliberately free-form rather than a
/// store taxonomy: letting people name lists gets categories of store for free,
/// and a StoreKind enum is a guess until speed-dials need one.
/// </summary>
[Table("Lists")]
public class GatherList
{
    public const int MaxNameLength = 60;

    /// <summary>An emoji, matching the family shell's module icons (see registry.ts).</summary>
    public const int MaxIconLength = 32;

    /// <summary>A theme token name the client resolves; the server never interprets it.</summary>
    public const int MaxColorLength = 32;

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    [MaxLength(MaxNameLength)]
    public required string Name { get; set; }

    [MaxLength(MaxIconLength)]
    public string? Icon { get; set; }

    [MaxLength(MaxColorLength)]
    public string? Color { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last activity on the list <em>by any means</em> - a rename, but also
    /// adding, checking, or clearing an item. A "last used" that froze at
    /// creation while the list churned daily would be a lie on a lists screen,
    /// which is the only thing that reads this.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public List<GatherItem> Items { get; set; } = [];
}

/// <summary>
/// One line on a list. Items only exist on a list - deleting the list takes
/// them with it (CASCADE).
/// </summary>
/// <remarks>
/// <see cref="NameNormalized"/> plus the unique index over
/// <c>(ListId, NameNormalized)</c> is what makes a re-add an upsert rather than
/// a second "milk" row: someone at the kiosk who can't see the list re-adds
/// what's already on it, and two rows is the wrong answer every time.
/// </remarks>
[Table("Items")]
[Index(nameof(ListId), nameof(NameNormalized), IsUnique = true)]
public partial class GatherItem
{
    /// <summary>
    /// Capped because it is half of a btree unique index. Long enough for the
    /// longest thing anyone writes on a shopping list and then some.
    /// </summary>
    public const int MaxNameLength = 120;

    /// <summary>"a dozen", "2 lbs", "x2" - free text, because those are all real answers.</summary>
    public const int MaxQuantityLength = 32;

    public const int MaxNoteLength = 200;

    private string name = "";

    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ListId { get; set; }
    public GatherList? List { get; set; }

    /// <summary>
    /// What gets displayed, trimmed. Setting it recomputes
    /// <see cref="NameNormalized"/> - the two cannot drift, so no write path can
    /// forget to normalize and slip a duplicate past the unique index.
    /// </summary>
    [MaxLength(MaxNameLength)]
    public required string Name
    {
        get => name;
        set
        {
            name = value.Trim();
            NameNormalized = Normalize(name);
        }
    }

    /// <summary>
    /// The identity half of the name: what two spellings of the same thing have
    /// in common. Never displayed. See <see cref="Normalize"/>.
    /// </summary>
    [MaxLength(MaxNameLength)]
    public string NameNormalized { get; private set; } = "";

    [MaxLength(MaxQuantityLength)]
    public string? Quantity { get; set; }

    [MaxLength(MaxNoteLength)]
    public string? Note { get; set; }

    public bool IsChecked { get; set; }

    /// <summary>When it was checked, and null whenever it isn't. Orders the checked block.</summary>
    public DateTimeOffset? CheckedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Case and spacing only: "  2%  Milk " and "2% milk" are the same item.
    /// </summary>
    /// <remarks>
    /// Deliberately not a stemmer. "Apple" and "apples" stay two items, because
    /// the cost of being wrong runs one way - a stray duplicate is a line
    /// someone crosses off, while silently merging "battery" into "batteries"
    /// loses a note or a quantity and nobody sees it happen.
    /// </remarks>
    public static string Normalize(string? value) =>
        value is null ? "" : Whitespace().Replace(value, " ").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
