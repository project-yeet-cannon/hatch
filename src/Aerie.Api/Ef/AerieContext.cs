using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

public class AerieContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EfEnvironmentReading>();

        base.OnModelCreating(modelBuilder);
    }
}
