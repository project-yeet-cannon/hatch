using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// The channel set for a camera.* entity, used by DiscoveryService's import
/// suggestions. Mirrors ThermostatChannelBuilder/LightChannelBuilder's shape:
/// a CameraFeed channel for the camera itself is always emitted, plus a
/// MotionState channel for the sibling motion binary_sensor.* entity when HA
/// grouped one under the same device (not every camera has a motion sensor).
/// </summary>
public static class CameraChannelBuilder
{
    /// <summary>
    /// Sibling binary_sensor.* suffixes that can back the MotionState channel,
    /// most-preferred first. A camera's plain _motion sensor fires on trees,
    /// rain, headlights and shadows; the on-camera AI _person sensor fires far
    /// less often, and every motion transition opens a modal on the kiosk, so
    /// the quieter sensor wins where the camera publishes one. Cameras without
    /// AI detection only ever expose _motion and land on the fallback.
    /// </summary>
    private static readonly string[] MotionSuffixesByPreference = ["_person", "_motion"];

    public static IReadOnlyList<DeviceChannelWriteRequest> Build(string cameraEntityId, IReadOnlyList<string> siblingEntityIds)
    {
        var channels = new List<DeviceChannelWriteRequest>
        {
            new(DeviceChannelMetric.CameraFeed, cameraEntityId, null, ChannelDirection.Read),
        };

        var motionEntity = PickMotionEntity(siblingEntityIds);
        if (motionEntity is not null)
            channels.Add(new(DeviceChannelMetric.MotionState, motionEntity, null, ChannelDirection.Read));

        return channels;
    }

    /// <summary>The sibling binary_sensor.* entity the MotionState channel should point at, or null when the device has none. Walks MotionSuffixesByPreference in order rather than taking whichever sensor happens to come first in the group, and sorts within a suffix so the pick doesn't depend on the caller's ordering.</summary>
    private static string? PickMotionEntity(IReadOnlyList<string> siblingEntityIds)
    {
        var sensors = siblingEntityIds
            .Where(id => id.StartsWith("binary_sensor.", StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return MotionSuffixesByPreference
            .Select(suffix => sensors.FirstOrDefault(id => id.EndsWith(suffix, StringComparison.Ordinal)))
            .FirstOrDefault(id => id is not null);
    }
}
