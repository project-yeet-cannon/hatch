using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;
using HADotNet.Core.Models;
using Newtonsoft.Json.Linq;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// The channel set for a climate.* entity, shared by DiscoveryService (Phase 2
/// import suggestions) and DeviceMappingSeeder (Phase 1 legacy backfill) so
/// the two don't drift. Capability channels (HvacMode/FanMode) are built from
/// the entity's own live hvac_modes/fan_modes attributes rather than a
/// hardcoded per-brand list, so a fan-less baseboard thermostat (Mysa) and a
/// multi-mode unit with a fan (a Honeywell T5) each end up with exactly the
/// channels they actually support.
/// </summary>
public static class ThermostatChannelBuilder
{
    /// <summary>
    /// Builds the standard channel set for one climate entity. <paramref name="climateState"/>
    /// is the entity's live HA state (used to read hvac_modes/fan_modes and decide whether a
    /// FanMode channel applies); pass null when it's unavailable (e.g. HA unreachable during
    /// import) to fall back to HvacMode-only with no AvailableOptions populated.
    /// </summary>
    public static IReadOnlyList<DeviceChannelWriteRequest> Build(
        string climateEntityId, IReadOnlyList<string> siblingEntityIds, StateObject? climateState)
    {
        var temperatureSensor = siblingEntityIds.FirstOrDefault(id =>
            id.StartsWith("sensor.", StringComparison.Ordinal) && id.EndsWith("_temperature", StringComparison.Ordinal));
        var humiditySensor = siblingEntityIds.FirstOrDefault(id =>
            id.StartsWith("sensor.", StringComparison.Ordinal) && id.EndsWith("_humidity", StringComparison.Ordinal));

        var channels = new List<DeviceChannelWriteRequest>
        {
            temperatureSensor is not null
                ? new(DeviceChannelMetric.Temperature, temperatureSensor, null, ChannelDirection.Read)
                : new(DeviceChannelMetric.Temperature, climateEntityId, "current_temperature", ChannelDirection.Read),
            humiditySensor is not null
                ? new(DeviceChannelMetric.Humidity, humiditySensor, null, ChannelDirection.Read)
                : new(DeviceChannelMetric.Humidity, climateEntityId, "current_humidity", ChannelDirection.Read),
            new(DeviceChannelMetric.SetpointTemperature, climateEntityId, "temperature", ChannelDirection.ReadWrite),
            new(DeviceChannelMetric.HvacAction, climateEntityId, "hvac_action", ChannelDirection.Read),
            new(DeviceChannelMetric.HvacMode, climateEntityId, "hvac_mode", ChannelDirection.ReadWrite, ReadOptions(climateState, "hvac_modes")),
        };

        var fanModes = ReadOptions(climateState, "fan_modes");
        if (fanModes is { Count: > 0 })
            channels.Add(new(DeviceChannelMetric.FanMode, climateEntityId, "fan_mode", ChannelDirection.ReadWrite, fanModes));

        return channels;
    }

    /// <summary>
    /// Reads a list-valued HA attribute (hvac_modes/fan_modes). HADotNet deserializes
    /// attributes into `object` via Newtonsoft, so a JSON array comes back as a JArray;
    /// the IEnumerable&lt;object&gt; branch is a defensive fallback for any other list
    /// shape rather than an observed case.
    /// </summary>
    public static IReadOnlyList<string>? ReadOptions(StateObject? state, string attribute)
    {
        if (state is null || !state.Attributes.TryGetValue(attribute, out var raw) || raw is null) return null;

        return raw switch
        {
            JArray array => array.ToObject<List<string>>(),
            IEnumerable<object> items => items.Select(i => i?.ToString() ?? "").ToList(),
            _ => null,
        };
    }
}
