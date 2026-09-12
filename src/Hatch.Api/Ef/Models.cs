using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Ef;

[Table("EnvironmentReadings")]
[Index(nameof(EntityId), nameof(Timestamp), IsUnique = true, Name = UniqueIndexName)]
public class EfEnvironmentReading
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public required string EntityId { get; set; }
    public required DateTimeOffset Timestamp { get; set; }

    public decimal? Temperature { get; set; }
    public decimal? Humidity { get; set; }

    public decimal? DesiredTemperature { get; set; }
    public bool IsHeating { get; set; }

    public const string UniqueIndexName = "IX_EnvironmentReadings_EntityId_Timestamp";
}

/// <summary>
/// Per-zone dashboard configuration. This is the source of truth for a zone's
/// display name, comfort band, ordering, and whether it appears on the
/// dashboard at all - none of which Home Assistant models. A <c>climate.*</c>
/// entity with no row here still works via sensible fallbacks (see ZoneService).
/// </summary>
[Table("ZoneConfigs")]
public class EfZoneConfig
{
    /// <summary>The Home Assistant entity id, e.g. "climate.living_room".</summary>
    [Key]
    public required string EntityId { get; set; }

    public required string DisplayName { get; set; }

    public decimal? ComfortLowF { get; set; }
    public decimal? ComfortHighF { get; set; }

    /// <summary>Ascending display order on the dashboard.</summary>
    public int SortOrder { get; set; }

    /// <summary>When false, the zone is excluded from the dashboard.</summary>
    public bool Included { get; set; } = true;
}
