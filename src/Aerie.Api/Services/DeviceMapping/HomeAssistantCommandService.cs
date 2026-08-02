using HADotNet.Core.Clients;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// Thin wrapper around HADotNet's ServiceClient, following
/// HomeAssistantStateReader's pattern (ServiceClient is sealed with
/// non-virtual methods, so it can't be substituted directly in a test). The
/// write side of every ReadWrite channel: PowerState (switch.*), and
/// SetpointTemperature/HvacMode/FanMode (climate.*) - see
/// DevicesController's channel-write endpoints.
/// </summary>
public interface IHomeAssistantCommandService
{
    Task SetSwitchAsync(string entityId, bool on);
    Task SetTemperatureAsync(string entityId, decimal temperature);
    Task SetHvacModeAsync(string entityId, string mode);
    Task SetFanModeAsync(string entityId, string mode);
}

public class HomeAssistantCommandService(ServiceClient service) : IHomeAssistantCommandService
{
    public Task SetSwitchAsync(string entityId, bool on) =>
        service.CallService("switch", on ? "turn_on" : "turn_off", new { entity_id = entityId });

    public Task SetTemperatureAsync(string entityId, decimal temperature) =>
        service.CallService("climate", "set_temperature", new { entity_id = entityId, temperature });

    public Task SetHvacModeAsync(string entityId, string mode) =>
        service.CallService("climate", "set_hvac_mode", new { entity_id = entityId, hvac_mode = mode });

    public Task SetFanModeAsync(string entityId, string mode) =>
        service.CallService("climate", "set_fan_mode", new { entity_id = entityId, fan_mode = mode });
}
