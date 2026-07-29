using HADotNet.Core.Clients;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// Thin wrapper around HADotNet's ServiceClient, following
/// HomeAssistantStateReader's pattern (ServiceClient is sealed with
/// non-virtual methods, so it can't be substituted directly in a test). The
/// write side of a ReadWrite channel, first consumed by DevicesController's
/// switch power endpoint - see docs/device-architecture.md's "Future work"
/// section, which called this out as the layer's one missing capability.
/// </summary>
public interface IHomeAssistantCommandService
{
    Task SetSwitchAsync(string entityId, bool on);
}

public class HomeAssistantCommandService(ServiceClient service) : IHomeAssistantCommandService
{
    public Task SetSwitchAsync(string entityId, bool on) =>
        service.CallService("switch", on ? "turn_on" : "turn_off", new { entity_id = entityId });
}
