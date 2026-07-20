using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

public class AerieContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<EfEnvironmentReading> EnvironmentReadings => Set<EfEnvironmentReading>();
    public DbSet<EfZoneConfig> ZoneConfigs => Set<EfZoneConfig>();

    public DbSet<EfZone> Zones => Set<EfZone>();
    public DbSet<EfDevice> Devices => Set<EfDevice>();
    public DbSet<EfDeviceChannel> DeviceChannels => Set<EfDeviceChannel>();
    public DbSet<EfSiteSetting> SiteSettings => Set<EfSiteSetting>();
    public DbSet<EfMeasurement> Measurements => Set<EfMeasurement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EfEnvironmentReading>();
        modelBuilder.Entity<EfZoneConfig>();

        modelBuilder.Entity<EfZone>();
        modelBuilder.Entity<EfSiteSetting>();

        modelBuilder.Entity<EfDevice>()
            .HasOne(d => d.Zone)
            .WithMany()
            .HasForeignKey(d => d.ZoneId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<EfDeviceChannel>()
            .HasOne(c => c.Device)
            .WithMany()
            .HasForeignKey(c => c.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfMeasurement>()
            .HasOne(m => m.Channel)
            .WithMany()
            .HasForeignKey(m => m.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        base.OnModelCreating(modelBuilder);
    }
}
