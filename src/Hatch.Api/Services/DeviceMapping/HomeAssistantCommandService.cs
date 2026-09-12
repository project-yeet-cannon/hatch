using HADotNet.Core.Clients;

namespace Hatch.Api.Services.DeviceMapping;

/// <summary>
/// Thin wrapper around HADotNet's ServiceClient, following
/// HomeAssistantStateReader's pattern (ServiceClient is sealed with
/// non-virtual methods, so it can't be substituted directly in a test). The
/// write side of every ReadWrite channel: PowerState (switch.*/light.*),
/// SetpointTemperature/HvacMode/FanMode (climate.*), Scene (scene.*), and
/// MediaPlayback (media_player.*) - see DevicesController's channel-write
/// endpoints.
/// </summary>
public interface IHomeAssistantCommandService
{
    Task SetPowerAsync(string entityId, bool on);
    Task SetTemperatureAsync(string entityId, decimal temperature);
    Task SetHvacModeAsync(string entityId, string mode);
    Task SetFanModeAsync(string entityId, string mode);
    Task TriggerSceneAsync(string entityId);
    Task PlayMediaAsync(string entityId, string mediaContentId, string mediaContentType);
}

public class HomeAssistantCommandService(ServiceClient service) : IHomeAssistantCommandService
{
    /// <summary>The HA service domain is the entity id's own prefix (switch.foo -> "switch", light.foo -> "light"), so this works for any domain that implements turn_on/turn_off rather than just switch.*.</summary>
    public Task SetPowerAsync(string entityId, bool on) =>
        service.CallService(Domain(entityId), on ? "turn_on" : "turn_off", new { entity_id = entityId });

    public Task SetTemperatureAsync(string entityId, decimal temperature) =>
        service.CallService("climate", "set_temperature", new { entity_id = entityId, temperature });

    public Task SetHvacModeAsync(string entityId, string mode) =>
        service.CallService("climate", "set_hvac_mode", new { entity_id = entityId, hvac_mode = mode });

    public Task SetFanModeAsync(string entityId, string mode) =>
        service.CallService("climate", "set_fan_mode", new { entity_id = entityId, fan_mode = mode });

    public Task TriggerSceneAsync(string entityId) =>
        service.CallService("scene", "turn_on", new { entity_id = entityId });

    /// <summary>Plays one item on a media_player.* entity. The content id is resolved HA-side (it fetches the URL, or resolves a media-source:// id), so a network-library path only has to be reachable from the HA host, not from Hatch.</summary>
    public Task PlayMediaAsync(string entityId, string mediaContentId, string mediaContentType) =>
        service.CallService("media_player", "play_media", new
        {
            entity_id = entityId,
            media_content_id = mediaContentId,
            media_content_type = mediaContentType,
        });

    private static string Domain(string entityId) => entityId.Split('.', 2)[0];
}
