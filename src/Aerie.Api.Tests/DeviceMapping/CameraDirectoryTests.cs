using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>
/// Covers CameraDirectory.GetCamerasAsync - which cameras reach the kiosk's
/// button row, and which of them report an address to stream from
/// (docs/camera-devices-architecture.md). Against an EF Core InMemory database, as
/// RoutineServiceTests does for the routine row beside it.
/// </summary>
public class CameraDirectoryTests
{
    private static IDbContextFactory<AerieContext> NewFactory() =>
        new TestDbContextFactory(new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Guid AddCamera(AerieContext db, string name, bool enabled = true, DeviceKind? kind = DeviceKind.Camera)
    {
        var id = Guid.NewGuid();
        db.Devices.Add(new EfDevice { Id = id, Name = name, Kind = kind, Enabled = enabled });
        db.DeviceChannels.Add(new EfDeviceChannel
        {
            Id = Guid.NewGuid(),
            DeviceId = id,
            Metric = DeviceChannelMetric.CameraFeed,
            HaEntityId = $"camera.{name.ToLowerInvariant()}",
            Direction = ChannelDirection.Read,
        });
        return id;
    }

    [Fact]
    public async Task GetCamerasAsync_ReturnsEnabledCameras_OrderedByName()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddCamera(db, "Driveway");
            AddCamera(db, "Back door");
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.Equal(["Back door", "Driveway"], result.Select(c => c.Name));
    }

    [Fact]
    public async Task GetCamerasAsync_ExcludesDisabledDevices()
    {
        // Same predicate CameraController.Stream uses: a disabled camera 404s
        // there, so it must not be offered a button here.
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddCamera(db, "Driveway");
            AddCamera(db, "Old shed", enabled: false);
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.Equal(["Driveway"], result.Select(c => c.Name));
    }

    [Fact]
    public async Task GetCamerasAsync_ExcludesDevicesWithoutACameraFeedChannel()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddCamera(db, "Driveway");

            var lampId = Guid.NewGuid();
            db.Devices.Add(new EfDevice { Id = lampId, Name = "Porch lamp", Kind = DeviceKind.SmartSwitch });
            db.DeviceChannels.Add(new EfDeviceChannel
            {
                Id = Guid.NewGuid(),
                DeviceId = lampId,
                Metric = DeviceChannelMetric.PowerState,
                HaEntityId = "switch.porch_lamp",
                Direction = ChannelDirection.ReadWrite,
            });
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.Equal(["Driveway"], result.Select(c => c.Name));
    }

    [Fact]
    public async Task GetCamerasAsync_KeysOnTheChannel_NotOnDeviceKind()
    {
        // Kind is inferred at import and editable by hand; the CameraFeed
        // channel is what the relay needs. A camera whose kind was changed still
        // streams, so it still gets a button.
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddCamera(db, "Driveway", kind: null);
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.Equal(["Driveway"], result.Select(c => c.Name));
    }

    [Fact]
    public async Task GetCamerasAsync_ReportsNotConfigured_WhenThereIsNoConnectionRow()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            AddCamera(db, "Driveway");
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.False(Assert.Single(result).IsConfigured);
    }

    [Fact]
    public async Task GetCamerasAsync_ReportsNotConfigured_WhenTheConnectionHasNoHost()
    {
        // The row exists - discovery writes one - but nothing on it says where
        // the camera is. This is exactly the case CameraController answers 409 to.
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var id = AddCamera(db, "Driveway");
            db.CameraConnections.Add(new EfCameraConnection { DeviceId = id, Username = "admin" });
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.False(Assert.Single(result).IsConfigured);
    }

    [Fact]
    public async Task GetCamerasAsync_ReportsConfigured_WhenOnlyHomeAssistantSuppliedTheHost()
    {
        // Nobody typed anything into the admin form, and the camera still
        // streams. Reporting it as unconfigured would put a "not set up yet"
        // message over a working feed.
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var id = AddCamera(db, "Driveway");
            db.CameraConnections.Add(new EfCameraConnection { DeviceId = id, DiscoveredHost = "192.168.1.51" });
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.True(Assert.Single(result).IsConfigured);
    }

    [Fact]
    public async Task GetCamerasAsync_ReportsConfigured_PerCamera()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var driveway = AddCamera(db, "Driveway");
            AddCamera(db, "Back door");
            db.CameraConnections.Add(new EfCameraConnection { DeviceId = driveway, Host = "192.168.1.51" });
            await db.SaveChangesAsync();
        }

        var result = await new CameraDirectory(factory).GetCamerasAsync(CancellationToken.None);

        Assert.Equal([("Back door", false), ("Driveway", true)], result.Select(c => (c.Name, c.IsConfigured)));
    }

    [Fact]
    public async Task GetCamerasAsync_ReturnsEmpty_WhenThereAreNoCameras()
    {
        var result = await new CameraDirectory(NewFactory()).GetCamerasAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
        public Task<AerieContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AerieContext(options));
    }
}
