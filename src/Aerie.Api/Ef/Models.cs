using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

[Table("Users")]
[Index(nameof(ApiKey), IsUnique = true)]
public class EfUser
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? ApiKey { get; set; }
}
