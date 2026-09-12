using Hatch.Api.Ef;
using Hatch.Api.Models.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Services.DeviceMapping;

public interface ICameraDirectory
{
    Task<IReadOnlyList<CameraSummary>> GetCamerasAsync(CancellationToken ct);
}

/// <summary>
/// The kiosk's read path for cameras (docs/camera-devices-architecture.md) - the
/// button row under the routines. Mirrors RoutineService: a dashboard-shaped
/// projection, fetched alongside everything else in DashboardService's fan-out.
///
/// The predicate is a CameraFeed channel on an enabled device, which is
/// verbatim what <see cref="Hatch.Api.Controllers.CameraController"/> asks
/// before it streams or 404s - deliberately, and not
/// <see cref="DeviceKind.Camera"/>. Keying the two on different questions is
/// how you end up with a button on the wall that answers 404, and the kind is
/// the looser of the two: it is inferred at import (see
/// <see cref="DiscoveryService"/>) and editable by hand afterwards, while the
/// channel is the thing the relay actually needs.
///
/// Every such camera gets a button, configured or not. A camera imported from
/// discovery that nobody has finished filling in on the admin form is a normal
/// state, and a button that says so out loud is how anyone finds out - the
/// alternative is a camera that is simply missing from the wall, which looks
/// identical to one that was never imported.
/// </summary>
public class CameraDirectory(IDbContextFactory<AppDbContext> dbFactory) : ICameraDirectory
{
    public async Task<IReadOnlyList<CameraSummary>> GetCamerasAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // By name: cameras have no SortOrder of their own, and a wall of
        // buttons that reorders itself between polls is worse than one in an
        // arbitrary but fixed order.
        var cameras = await db.DeviceChannels.AsNoTracking()
            .Where(c => c.Metric == DeviceChannelMetric.CameraFeed && c.Device!.Enabled)
            .Select(c => new { c.DeviceId, c.Device!.Name })
            .Distinct()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        if (cameras.Count == 0) return [];

        var deviceIds = cameras.Select(c => c.DeviceId).ToList();
        var connections = await db.CameraConnections.AsNoTracking()
            .Where(c => deviceIds.Contains(c.DeviceId))
            .ToDictionaryAsync(c => c.DeviceId, ct);

        return cameras
            .Select(c => new CameraSummary(
                c.DeviceId,
                c.Name,
                IsConfigured: CameraRtspUrl.TryBuild(connections.GetValueOrDefault(c.DeviceId), out _)))
            .ToList();
    }
}
