using Hatch.Api.Common;
using Hatch.Api.Controllers;
using Hatch.Api.Ef;
using Hatch.Api.Models.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.DeviceMapping;

/// <summary>
/// docs/camera-devices-architecture.md. The rule worth pinning here is the
/// three-state password: the API can never hand a stored one back, so the form
/// cannot pre-fill the box, so a save that did not touch it must not clear it.
/// Get that wrong and every edit to a camera's host silently breaks its feed.
/// </summary>
public class CameraConnectionEndpointTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DevicesController NewController(AppDbContext db) =>
        new(db, null!, null!, null!);

    private static async Task<Guid> NewCamera(AppDbContext db)
    {
        var device = new EfDevice { Name = "Front door", Kind = DeviceKind.Camera };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private static CameraConnectionDto Value(ActionResult<CameraConnectionDto> result) =>
        result.Value ?? throw new InvalidOperationException($"Expected a value, got {result.Result?.GetType().Name}");

    [Fact]
    public async Task An_unconfigured_camera_answers_with_the_defaults()
    {
        // The normal state between importing a camera and filling the form in.
        // It has to answer, because the form renders from this.
        await using var db = NewContext();
        var id = await NewCamera(db);

        var dto = Value(await NewController(db).GetCameraConnection(id, CancellationToken.None));

        Assert.Null(dto.EffectiveHost);
        Assert.False(dto.HasPassword);
        Assert.Equal(554, dto.Port);
        Assert.Equal("/h264Preview_01_sub", dto.StreamPath);
    }

    [Fact]
    public async Task An_unknown_device_is_not_found()
    {
        await using var db = NewContext();

        var result = await NewController(db).GetCameraConnection(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Saving_stores_the_password_protected_and_never_returns_it()
    {
        await using var db = NewContext();
        var id = await NewCamera(db);
        var controller = NewController(db);

        var dto = Value(await controller.UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("10.0.0.9", 554, "/h264Preview_01_sub", "admin", "hunter2"), CancellationToken.None));

        Assert.True(dto.HasPassword);

        var stored = await db.CameraConnections.AsNoTracking().SingleAsync(c => c.DeviceId == id);
        // Protected, tagged, and recoverable - the three things the stream needs.
        Assert.StartsWith("v1:", stored.PasswordProtected, StringComparison.Ordinal);
        Assert.Equal("hunter2", SecretProtector.Unprotect(stored.PasswordProtected));
    }

    [Fact]
    public async Task A_save_that_omits_the_password_leaves_it_alone()
    {
        // The whole reason Password is nullable. This is what the form sends
        // when someone corrects a typo in the host.
        await using var db = NewContext();
        var id = await NewCamera(db);
        var controller = NewController(db);

        await controller.UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("10.0.0.9", 554, "/sub", "admin", "hunter2"), CancellationToken.None);

        var dto = Value(await controller.UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("10.0.0.50", 554, "/sub", "admin", null), CancellationToken.None));

        Assert.True(dto.HasPassword);
        Assert.Equal("10.0.0.50", dto.EffectiveHost);
        var stored = await db.CameraConnections.AsNoTracking().SingleAsync(c => c.DeviceId == id);
        Assert.Equal("hunter2", SecretProtector.Unprotect(stored.PasswordProtected));
    }

    [Fact]
    public async Task An_empty_password_clears_it()
    {
        // For a camera whose RTSP is anonymous. Distinct from omitting it.
        await using var db = NewContext();
        var id = await NewCamera(db);
        var controller = NewController(db);

        await controller.UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("10.0.0.9", 554, "/sub", "admin", "hunter2"), CancellationToken.None);

        var dto = Value(await controller.UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("10.0.0.9", 554, "/sub", "admin", ""), CancellationToken.None));

        Assert.False(dto.HasPassword);
    }

    [Fact]
    public async Task Clearing_the_host_hands_the_camera_back_to_home_assistant()
    {
        // What the hint under the field promises. The override is stored as
        // null rather than "", so the discovered host takes over again.
        await using var db = NewContext();
        var id = await NewCamera(db);
        db.CameraConnections.Add(new EfCameraConnection { DeviceId = id, DiscoveredHost = "10.0.0.9", Host = "10.0.0.50" });
        await db.SaveChangesAsync();

        var dto = Value(await NewController(db).UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("   ", 554, "/sub", "admin", null), CancellationToken.None));

        Assert.Null(dto.Host);
        Assert.Equal("10.0.0.9", dto.EffectiveHost);
    }

    [Fact]
    public async Task A_discovery_refresh_does_not_clobber_an_override()
    {
        await using var db = NewContext();
        var id = await NewCamera(db);
        db.CameraConnections.Add(new EfCameraConnection { DeviceId = id, DiscoveredHost = "10.0.0.9", Host = "10.0.0.50" });
        await db.SaveChangesAsync();

        var dto = Value(await NewController(db).GetCameraConnection(id, CancellationToken.None));

        Assert.Equal("10.0.0.50", dto.EffectiveHost);
        Assert.Equal("10.0.0.9", dto.DiscoveredHost);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(99999)]
    public async Task An_impossible_port_is_stored_as_554(int? port)
    {
        // Stored rather than only corrected at stream time, so the form shows
        // what the stream will actually use.
        await using var db = NewContext();
        var id = await NewCamera(db);

        var dto = Value(await NewController(db).UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("10.0.0.9", port, "/sub", "admin", null), CancellationToken.None));

        Assert.Equal(554, dto.Port);
    }

    [Fact]
    public async Task A_blank_stream_path_falls_back_to_the_reolink_sub_stream()
    {
        await using var db = NewContext();
        var id = await NewCamera(db);

        var dto = Value(await NewController(db).UpsertCameraConnection(
            id, new CameraConnectionWriteRequest("10.0.0.9", 554, "  ", "admin", null), CancellationToken.None));

        Assert.Equal("/h264Preview_01_sub", dto.StreamPath);
    }

    [Fact]
    public async Task Importing_a_camera_seeds_the_host_home_assistant_reported()
    {
        // So a camera imported from discovery needs only a credential typed in.
        await using var db = NewContext();
        var controller = NewController(db);

        var created = await controller.Create(
            new DeviceWriteRequest("Front door", DeviceKind.Camera, null, "ha-device-1", true, "10.0.0.9"),
            CancellationToken.None);
        var device = ((created.Result as CreatedAtActionResult)!.Value as DeviceDto)!;

        var dto = Value(await controller.GetCameraConnection(device.Id, CancellationToken.None));
        Assert.Equal("10.0.0.9", dto.DiscoveredHost);
        Assert.Equal("10.0.0.9", dto.EffectiveHost);
        Assert.Null(dto.Host);
    }

    [Fact]
    public async Task Importing_a_non_camera_stores_no_connection()
    {
        await using var db = NewContext();
        var controller = NewController(db);

        await controller.Create(
            new DeviceWriteRequest("Lamp", DeviceKind.Light, null, "ha-device-2", true, "10.0.0.9"),
            CancellationToken.None);

        Assert.Empty(await db.CameraConnections.ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_camera_takes_its_password_with_it()
    {
        // Cascade is configured in OnModelCreating; this is the behaviour that
        // configuration exists for, and an orphaned credential row is not
        // something anything else in the app would notice.
        await using var db = NewContext();
        var id = await NewCamera(db);
        db.CameraConnections.Add(new EfCameraConnection
        {
            DeviceId = id,
            Host = "10.0.0.9",
            PasswordProtected = SecretProtector.Protect("hunter2"),
        });
        await db.SaveChangesAsync();

        db.Devices.Remove(await db.Devices.SingleAsync(d => d.Id == id));
        await db.SaveChangesAsync();

        Assert.Empty(await db.CameraConnections.ToListAsync());
    }
}
