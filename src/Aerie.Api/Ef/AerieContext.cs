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
    public DbSet<EfStateChange> StateChanges => Set<EfStateChange>();

    public DbSet<EfRoutine> Routines => Set<EfRoutine>();
    public DbSet<EfRoutineAction> RoutineActions => Set<EfRoutineAction>();

    public DbSet<EfCommand> Commands => Set<EfCommand>();
    public DbSet<EfControlDecision> ControlDecisions => Set<EfControlDecision>();
    public DbSet<EfControlOverride> ControlOverrides => Set<EfControlOverride>();

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
            .WithMany(d => d.Channels)
            .HasForeignKey(c => c.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfMeasurement>()
            .HasOne(m => m.Channel)
            .WithMany()
            .HasForeignKey(m => m.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfStateChange>()
            .HasOne(s => s.Channel)
            .WithMany()
            .HasForeignKey(s => s.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfRoutineAction>()
            .HasOne(a => a.Routine)
            .WithMany(r => r.Actions)
            .HasForeignKey(a => a.RoutineId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfRoutineAction>()
            .HasOne(a => a.Channel)
            .WithMany()
            .HasForeignKey(a => a.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        // Commands cascade with their channel, the same way Measurements and
        // StateChanges do - a deleted channel takes both halves of its history
        // (what we told it, what it read) with it rather than leaving one
        // orphaned half that can never be joined against the other again.
        modelBuilder.Entity<EfCommand>()
            .HasOne(c => c.Channel)
            .WithMany()
            .HasForeignKey(c => c.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        // A decision, by contrast, is not the command's parent - it's the
        // reasoning that happened to produce it. Deleting one leaves the
        // actuation record intact, just unexplained.
        modelBuilder.Entity<EfCommand>()
            .HasOne(c => c.Decision)
            .WithMany(d => d.Commands)
            .HasForeignKey(c => c.DecisionId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<EfControlOverride>()
            .HasOne(o => o.Command)
            .WithMany()
            .HasForeignKey(o => o.CommandId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfControlOverride>()
            .HasOne(o => o.Channel)
            .WithMany()
            .HasForeignKey(o => o.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfControlOverride>()
            .HasOne(o => o.Device)
            .WithMany()
            .HasForeignKey(o => o.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        base.OnModelCreating(modelBuilder);
    }
}
