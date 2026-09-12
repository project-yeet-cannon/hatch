using Hatch.Api.Ef;
using Hatch.Api.Models.DeviceMapping;

namespace Hatch.Api.Services.DeviceMapping;

/// <summary>
/// The channel set for a switch.* entity, used by DiscoveryService's import
/// suggestions. Mirrors LightChannelBuilder's shape: one PowerState channel
/// for the switch itself, plus one PowerState channel per sibling light.*
/// entity HA already grouped under the same device. Needed because switch.*
/// is checked before light.* in InferKind's precedence order, so without
/// this a device carrying both (e.g. a garage unit with a motion-detection
/// switch alongside its light) would have the light silently dropped from
/// the suggestion.
/// </summary>
public static class SwitchChannelBuilder
{
    public static IReadOnlyList<DeviceChannelWriteRequest> Build(string switchEntityId, IReadOnlyList<string> siblingEntityIds)
    {
        var channels = new List<DeviceChannelWriteRequest>
        {
            new(DeviceChannelMetric.PowerState, switchEntityId, null, ChannelDirection.ReadWrite),
        };

        channels.AddRange(siblingEntityIds
            .Where(id => id.StartsWith("light.", StringComparison.Ordinal))
            .Select(id => new DeviceChannelWriteRequest(DeviceChannelMetric.PowerState, id, null, ChannelDirection.ReadWrite)));

        return channels;
    }
}
