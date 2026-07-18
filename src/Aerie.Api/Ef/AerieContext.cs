using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

public class AerieContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<EfEnvironmentReading> EnvironmentReadings => Set<EfEnvironmentReading>();
    public DbSet<EfZoneConfig> ZoneConfigs => Set<EfZoneConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EfEnvironmentReading>();
        modelBuilder.Entity<EfZoneConfig>();

        base.OnModelCreating(modelBuilder);
    }
}
