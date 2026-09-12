using Hatch.Api.Ef;
using Hatch.Api.Models.DeviceMapping;

namespace Hatch.Api.Services.DeviceMapping;

/// <summary>
/// The channel set for a light.* entity, used by DiscoveryService's import
/// suggestions. Mirrors ThermostatChannelBuilder's shape: one PowerState
/// channel for the light itself, plus one Scene channel per sibling scene.*
/// entity HA already grouped under the same device (e.g. a Hue light's
/// "Nightlight"/"Rolling hills" scenes).
/// </summary>
public static class LightChannelBuilder
{
    public static IReadOnlyList<DeviceChannelWriteRequest> Build(string lightEntityId, IReadOnlyList<string> siblingEntityIds)
    {
        var channels = new List<DeviceChannelWriteRequest>
        {
            new(DeviceChannelMetric.PowerState, lightEntityId, null, ChannelDirection.ReadWrite),
        };

        channels.AddRange(siblingEntityIds
            .Where(id => id.StartsWith("scene.", StringComparison.Ordinal))
            .Select(id => new DeviceChannelWriteRequest(DeviceChannelMetric.Scene, id, null, ChannelDirection.ReadWrite)));

        return channels;
    }
}
