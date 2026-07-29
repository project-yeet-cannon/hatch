using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;
using HADotNet.Core.Clients;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aerie.Api.Services.DeviceMapping;

public interface IDiscoveryService
{
    /// <summary>HA devices with no matching EfDevice.HaDeviceId yet, grouped by HA device id, with a suggested name/kind/channels for one-click import.</summary>
    Task<IReadOnlyList<UnmappedHaDevice>> GetUnmappedAsync(CancellationToken ct = default);
}

/// <summary>
/// Resolves HA's entity->device grouping via the template endpoint (the HA REST
/// API has no direct "list devices" call - see docs/device-architecture.md's
/// Device Discovery section for why the template approach was chosen over the
/// websocket device-registry API) and diffs it against Devices already imported
/// into Aerie, so the admin import flow (Phase 3) can show what's left to map.
/// </summary>
public class DiscoveryService(TemplateClient template, AerieContext db) : IDiscoveryService
{
    // Grouping is computed HA-side via device_id()/device_attr() rather than
    // fetched entity-by-entity, since the HA REST API has no bulk device-registry
    // endpoint. Entities with no owning device (helpers, sun.sun, etc.) are
    // filtered out here - they aren't "devices" in the sense this feature cares about.
    private const string GroupingTemplate = """
        {% set ns = namespace(items=[]) %}
        {% for s in states %}
          {% set did = device_id(s.entity_id) %}
          {% if did %}
            {% set ns.items = ns.items + [{'entity_id': s.entity_id, 'device_id': did, 'device_name': device_attr(did, 'name')}] %}
          {% endif %}
        {% endfor %}
        {{ ns.items | tojson }}
        """;

    public async Task<IReadOnlyList<UnmappedHaDevice>> GetUnmappedAsync(CancellationToken ct = default)
    {
        var raw = await template.RenderTemplate(GroupingTemplate);
        var rows = JsonConvert.DeserializeObject<List<EntityDeviceRow>>(raw) ?? [];

        var mappedHaDeviceIds = await db.Devices
            .Where(d => d.HaDeviceId != null)
            .Select(d => d.HaDeviceId!)
            .ToListAsync(ct);
        var mapped = mappedHaDeviceIds.ToHashSet();

        return rows
            .GroupBy(r => r.DeviceId)
            .Where(g => !mapped.Contains(g.Key))
            .Select(BuildSuggestion)
            .OrderBy(d => d.SuggestedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static UnmappedHaDevice BuildSuggestion(IGrouping<string, EntityDeviceRow> group)
    {
        var entityIds = group.Select(r => r.EntityId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var name = group.Select(r => r.DeviceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? group.Key;

        var climateEntity = entityIds.FirstOrDefault(id => id.StartsWith("climate.", StringComparison.Ordinal));
        if (climateEntity is not null)
            return new UnmappedHaDevice(group.Key, name, DeviceKind.Thermostat, entityIds, ThermostatChannels(climateEntity, entityIds));

        var switchEntity = entityIds.FirstOrDefault(id => id.StartsWith("switch.", StringComparison.Ordinal));
        if (switchEntity is not null)
            return new UnmappedHaDevice(group.Key, name, DeviceKind.SmartSwitch, entityIds, SwitchChannels(switchEntity));

        var sensorChannels = entityIds.Select(SensorChannel).OfType<DeviceChannelWriteRequest>().ToList();
        var kind = sensorChannels.Count > 0 ? DeviceKind.Hygrometer : (DeviceKind?)null;

        return new UnmappedHaDevice(group.Key, name, kind, entityIds, sensorChannels);
    }

    /// <summary>
    /// Mirrors DeviceMappingSeeder's climate.* channel set (Phase 1), but prefers sibling
    /// sensor.* entities for current temperature/humidity when the HA device exposes them
    /// separately (e.g. newer Mysa baseboard thermostats) rather than as climate attributes.
    /// </summary>
    private static IReadOnlyList<DeviceChannelWriteRequest> ThermostatChannels(string climateEntityId, IReadOnlyList<string> entityIds)
    {
        var temperatureSensor = entityIds.FirstOrDefault(id =>
            id.StartsWith("sensor.", StringComparison.Ordinal) && id.EndsWith("_temperature", StringComparison.Ordinal));
        var humiditySensor = entityIds.FirstOrDefault(id =>
            id.StartsWith("sensor.", StringComparison.Ordinal) && id.EndsWith("_humidity", StringComparison.Ordinal));

        return
        [
            temperatureSensor is not null
                ? new(DeviceChannelMetric.Temperature, temperatureSensor, null, ChannelDirection.Read)
                : new(DeviceChannelMetric.Temperature, climateEntityId, "current_temperature", ChannelDirection.Read),
            humiditySensor is not null
                ? new(DeviceChannelMetric.Humidity, humiditySensor, null, ChannelDirection.Read)
                : new(DeviceChannelMetric.Humidity, climateEntityId, "current_humidity", ChannelDirection.Read),
            new(DeviceChannelMetric.SetpointTemperature, climateEntityId, "temperature", ChannelDirection.ReadWrite),
            new(DeviceChannelMetric.HvacAction, climateEntityId, "hvac_action", ChannelDirection.Read),
        ];
    }

    /// <summary>A plain HA switch.* entity: bare entity state ("on"/"off"), no sub-attribute, read-write.</summary>
    private static IReadOnlyList<DeviceChannelWriteRequest> SwitchChannels(string entityId) =>
    [
        new(DeviceChannelMetric.PowerState, entityId, null, ChannelDirection.ReadWrite),
    ];

    /// <summary>Hygrometer-style entity: metric lives in the suffix, bare entity state (no HaAttribute) - see EnvironmentService.MapFromHa for the pattern this generalizes.</summary>
    private static DeviceChannelWriteRequest? SensorChannel(string entityId) => entityId switch
    {
        _ when entityId.EndsWith("_temperature", StringComparison.Ordinal) => new(DeviceChannelMetric.Temperature, entityId, null, ChannelDirection.Read),
        _ when entityId.EndsWith("_humidity", StringComparison.Ordinal) => new(DeviceChannelMetric.Humidity, entityId, null, ChannelDirection.Read),
        _ when entityId.EndsWith("_battery", StringComparison.Ordinal) => new(DeviceChannelMetric.Battery, entityId, null, ChannelDirection.Read),
        _ => null,
    };

    private sealed record EntityDeviceRow(
        [property: JsonProperty("entity_id")] string EntityId,
        [property: JsonProperty("device_id")] string DeviceId,
        [property: JsonProperty("device_name")] string? DeviceName);
}
