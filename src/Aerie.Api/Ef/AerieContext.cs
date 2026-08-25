using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

public class AerieContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<EfEnvironmentReading> EnvironmentReadings => Set<EfEnvironmentReading>();
    public DbSet<EfZoneConfig> ZoneConfigs => Set<EfZoneConfig>();

    public DbSet<EfZone> Zones => Set<EfZone>();
    public DbSet<EfDevice> Devices => Set<EfDevice>();
    public DbSet<EfDeviceChannel> DeviceChannels => Set<EfDeviceChannel>();
    public DbSet<EfCameraConnection> CameraConnections => Set<EfCameraConnection>();
    public DbSet<EfSiteSetting> SiteSettings => Set<EfSiteSetting>();
    public DbSet<EfMeasurement> Measurements => Set<EfMeasurement>();
    public DbSet<EfStateChange> StateChanges => Set<EfStateChange>();

    public DbSet<EfRoutine> Routines => Set<EfRoutine>();
    public DbSet<EfRoutineAction> RoutineActions => Set<EfRoutineAction>();

    public DbSet<EfPanel> Panels => Set<EfPanel>();
    public DbSet<EfPanelItem> PanelItems => Set<EfPanelItem>();
    public DbSet<EfPanelControlBinding> PanelControlBindings => Set<EfPanelControlBinding>();

    public DbSet<EfCalendarAccount> CalendarAccounts => Set<EfCalendarAccount>();
    public DbSet<EfCalendar> Calendars => Set<EfCalendar>();
    public DbSet<EfCalendarEvent> CalendarEvents => Set<EfCalendarEvent>();
    public DbSet<EfOAuthState> OAuthStates => Set<EfOAuthState>();

    public DbSet<EfWeatherAlert> WeatherAlerts => Set<EfWeatherAlert>();
    public DbSet<EfAirQualitySample> AirQualitySamples => Set<EfAirQualitySample>();

    public DbSet<EfAuthGrant> AuthGrants => Set<EfAuthGrant>();
    public DbSet<EfAuthInvite> AuthInvites => Set<EfAuthInvite>();

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

        // One-to-one, declared rather than inferred: DeviceId is both the key
        // and the foreign key, and EfDevice carries no inverse navigation, so
        // convention invents a second shadow FK column instead of reusing this
        // one. Cascade is the part that matters - deleting a camera has to take
        // its stored password with it, and an orphaned credential row is not
        // something anything else in the app would ever notice.
        modelBuilder.Entity<EfCameraConnection>()
            .HasOne(c => c.Device)
            .WithOne()
            .HasForeignKey<EfCameraConnection>(c => c.DeviceId)
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

        // A Panel's items are the panel - they're replaced wholesale on write
        // and mean nothing on their own, so they go with it. Same for a
        // Control's bindings, and for the channel a binding points at: a
        // deleted channel takes the binding with it rather than leaving a
        // control half-bound to a channel id that resolves to nothing.
        modelBuilder.Entity<EfPanelItem>()
            .HasOne(i => i.Panel)
            .WithMany(p => p.Items)
            .HasForeignKey(i => i.PanelId)
            .OnDelete(DeleteBehavior.Cascade);

        // Cascade rather than SetNull despite the nullable FK: a Routine item
        // with no RoutineId is not a degraded item, it's an item that renders
        // nothing and can never be tapped. Deleting the routine deletes the
        // entry that pointed at it.
        modelBuilder.Entity<EfPanelItem>()
            .HasOne(i => i.Routine)
            .WithMany()
            .HasForeignKey(i => i.RoutineId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfPanelControlBinding>()
            .HasOne(b => b.Item)
            .WithMany(i => i.Bindings)
            .HasForeignKey(b => b.ItemId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfPanelControlBinding>()
            .HasOne(b => b.Channel)
            .WithMany()
            .HasForeignKey(b => b.ChannelId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfOAuthState>();

        // Hazards hang off nothing: an alert is about the site's coordinates,
        // not about any device or zone, and both tables are written by the
        // sync job from whatever provider the settings name. Their keys are
        // (Source, provider id) and (Source, hour), declared on the entities.
        modelBuilder.Entity<EfWeatherAlert>();
        modelBuilder.Entity<EfAirQualitySample>();

        // Auth lives in the core context and the public schema on purpose: it
        // is infrastructure every module sits behind, and a Modules/ schema
        // would make every module depend on one module (Modules/README.md).
        // Neither entity has a relationship - a grant outlives the invite that
        // made it, which is why EfAuthInvite.RedeemedGrantId is a bare Guid.
        modelBuilder.Entity<EfAuthGrant>();
        modelBuilder.Entity<EfAuthInvite>();

        // A calendar has no meaning without the account whose grant reaches it,
        // and an event has none without its calendar - disconnecting an account
        // takes the whole subtree, cached events included.
        modelBuilder.Entity<EfCalendar>()
            .HasOne(c => c.Account)
            .WithMany(a => a.Calendars)
            .HasForeignKey(c => c.AccountId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EfCalendarEvent>()
            .HasOne(e => e.Calendar)
            .WithMany(c => c.Events)
            .HasForeignKey(e => e.CalendarId)
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
