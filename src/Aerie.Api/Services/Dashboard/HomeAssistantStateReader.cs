using HADotNet.Core.Clients;
using HADotNet.Core.Models;

namespace Aerie.Api.Services.Dashboard;

/// <summary>
/// Thin wrapper around HADotNet's StatesClient. StatesClient is sealed with
/// non-virtual methods, so it can't be substituted directly in a test - this
/// interface is what makes WeatherService's HA-call bounding (see
/// WeatherService.BoundedStateAsync) exercisable without a live Home
/// Assistant instance.
/// </summary>
public interface IHomeAssistantStateReader
{
    Task<StateObject?> TryGetStateAsync(string entityId, CancellationToken ct);
}

public class HomeAssistantStateReader(StatesClient states, ILogger<HomeAssistantStateReader> logger) : IHomeAssistantStateReader
{
    public async Task<StateObject?> TryGetStateAsync(string entityId, CancellationToken ct)
    {
        try
        {
            return await states.GetState(entityId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read Home Assistant entity {Entity}", entityId);
            return null;
        }
    }
}
