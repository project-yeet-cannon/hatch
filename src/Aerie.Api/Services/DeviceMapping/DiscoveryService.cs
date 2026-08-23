using Aerie.Api.Ef;
using Aerie.Api.Models.DeviceMapping;
using Aerie.Api.Services.Dashboard;
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
public class DiscoveryService(TemplateClient template, AerieContext db, IHomeAssistantStateReader stateReader) : IDiscoveryService
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

        var suggestions = await Task.WhenAll(rows
            .GroupBy(r => r.DeviceId)
            .Where(g => !mapped.Contains(g.Key))
            .Select(g => BuildSuggestion(g, ct)));

        return suggestions.OrderBy(d => d.SuggestedName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<UnmappedHaDevice> BuildSuggestion(IGrouping<string, EntityDeviceRow> group, CancellationToken ct)
    {
        var entityIds = group.Select(r => r.EntityId).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var name = group.Select(r => r.DeviceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? group.Key;

        if (InferKind(entityIds) is { } match)
        {
            IReadOnlyList<DeviceChannelWriteRequest> channels = match.Kind switch
            {
                DeviceKind.Thermostat => ThermostatChannelBuilder.Build(
                    match.AnchorEntityId, entityIds, await stateReader.TryGetStateAsync(match.AnchorEntityId, ct)),
                DeviceKind.Speaker => SpeakerChannels(match.AnchorEntityId),
                DeviceKind.SmartSwitch => SwitchChannelBuilder.Build(match.AnchorEntityId, entityIds),
                DeviceKind.Light => LightChannelBuilder.Build(match.AnchorEntityId, entityIds),
                DeviceKind.Camera => CameraChannelBuilder.Build(match.AnchorEntityId, entityIds),
                _ => throw new InvalidOperationException($"InferKind returned unhandled kind {match.Kind}"),
            };
            return new UnmappedHaDevice(group.Key, name, match.Kind, entityIds, channels);
        }

        var sensorChannels = entityIds.Select(SensorChannel).OfType<DeviceChannelWriteRequest>().ToList();
        var kind = sensorChannels.Count > 0 ? DeviceKind.Hygrometer : (DeviceKind?)null;

        return new UnmappedHaDevice(group.Key, name, kind, entityIds, sensorChannels);
    }

    /// <summary>Which entity-id-prefix-based DeviceKind a group of HA entities suggests, and the "anchor" entity that kind's channel builder is built around. Pure function of the entity-id list (no HA/DB calls) so the branch order and matches are directly unit-testable; the Hygrometer/no-kind fallback lives in BuildSuggestion since it depends on SensorChannel's per-entity mapping rather than a single anchor entity.</summary>
    public static KindMatch? InferKind(IReadOnlyList<string> entityIds)
    {
        var climateEntity = entityIds.FirstOrDefault(id => id.StartsWith("climate.", StringComparison.Ordinal));
        if (climateEntity is not null)
            return new KindMatch(DeviceKind.Thermostat, climateEntity);

        // Checked before switch.*: a Sonos speaker's HA device also carries
        // switch.* siblings (loudness, crossfade, TV autoplay), so sniffing for
        // switch first would import every speaker as a SmartSwitch.
        var mediaPlayerEntity = entityIds.FirstOrDefault(id => id.StartsWith("media_player.", StringComparison.Ordinal));
        if (mediaPlayerEntity is not null)
            return new KindMatch(DeviceKind.Speaker, mediaPlayerEntity);

        var switchEntity = entityIds.FirstOrDefault(id => id.StartsWith("switch.", StringComparison.Ordinal));
        if (switchEntity is not null)
            return new KindMatch(DeviceKind.SmartSwitch, switchEntity);

        var lightEntity = entityIds.FirstOrDefault(id => id.StartsWith("light.", StringComparison.Ordinal));
        if (lightEntity is not null)
            return new KindMatch(DeviceKind.Light, lightEntity);

        var cameraEntity = PickCameraAnchor(entityIds);
        if (cameraEntity is not null)
            return new KindMatch(DeviceKind.Camera, cameraEntity);

        return null;
    }

    /// <summary>Suffix of the camera.* entity to anchor a Camera device on when the group offers a choice. Reolink publishes one camera.* entity per stream profile (_fluent/_balanced/_clear, plus _snapshots_* still-image variants); only the low-res "Fluent" sub-stream is H.264, where the higher-res profiles are H.265 and won't play in a browser. Nothing enforces this suffix - it's a preference, not a requirement, so non-Reolink cameras fall through to the ordinal-first candidate.</summary>
    private const string SubStreamSuffix = "_fluent";

    /// <summary>The camera.* entity a Camera device's CameraFeed channel should point at. Picks the browser-playable sub-stream when the group has one, ignoring the _snapshots_* still-image entities (which also end in _fluent), and otherwise takes the ordinal-first camera entity. Sorts its own candidates rather than trusting the caller's ordering, so the anchor doesn't silently change when a previously-disabled profile entity starts reporting state.</summary>
    private static string? PickCameraAnchor(IReadOnlyList<string> entityIds)
    {
        var cameras = entityIds
            .Where(id => id.StartsWith("camera.", StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return cameras.FirstOrDefault(id =>
                   id.EndsWith(SubStreamSuffix, StringComparison.Ordinal)
                   && !id.Contains("_snapshots", StringComparison.Ordinal))
               ?? cameras.FirstOrDefault();
    }

    /// <summary>Result of InferKind: the inferred DeviceKind and the specific entity id its channel builder should be built around.</summary>
    public readonly record struct KindMatch(DeviceKind Kind, string AnchorEntityId);

    /// <summary>A media_player.* entity: bare entity state ("playing"/"paused"/"idle"), no sub-attribute, read-write (DevicesController.PlayMedia). The speaker's switch.*/number.* tuning siblings (loudness, bass, balance...) are deliberately left unmapped - they're setup knobs, not things Aerie drives.</summary>
    private static IReadOnlyList<DeviceChannelWriteRequest> SpeakerChannels(string entityId) =>
    [
        new(DeviceChannelMetric.MediaPlayback, entityId, null, ChannelDirection.ReadWrite),
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
