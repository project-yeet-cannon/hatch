using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// The channel set for a camera.* entity, used by DiscoveryService's import
/// suggestions. Mirrors ThermostatChannelBuilder/LightChannelBuilder's shape:
/// a CameraFeed channel for the camera itself is always emitted, plus a
/// MotionState channel for the sibling binary_sensor.*_motion entity when HA
/// grouped one under the same device (not every camera has a motion sensor).
/// </summary>
public static class CameraChannelBuilder
{
    public static IReadOnlyList<DeviceChannelWriteRequest> Build(string cameraEntityId, IReadOnlyList<string> siblingEntityIds)
    {
        var channels = new List<DeviceChannelWriteRequest>
        {
            new(DeviceChannelMetric.CameraFeed, cameraEntityId, null, ChannelDirection.Read),
        };

        var motionEntity = siblingEntityIds.FirstOrDefault(id =>
            id.StartsWith("binary_sensor.", StringComparison.Ordinal) && id.EndsWith("_motion", StringComparison.Ordinal));
        if (motionEntity is not null)
            channels.Add(new(DeviceChannelMetric.MotionState, motionEntity, null, ChannelDirection.Read));

        return channels;
    }
}
