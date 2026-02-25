using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

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
