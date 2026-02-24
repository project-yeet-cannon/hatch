using Aerie.Api.Common;
using Aerie.Api.Ef;
using Aerie.Api.Models.HomeAssistant;
using HADotNet.Core;
using HADotNet.Core.Clients;
using HADotNet.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class HomeAssistantController(TimeProvider time, ISecrets secrets) : ControllerBase
{
    [HttpGet]
    public async Task<object> Get()
    {
        var haKey = secrets.GetSecret("ha_key");
        if (haKey is null)
        {
            throw new Exception("Could not find ha_key in secrets");
        }

        var now = time.GetUtcNow();

        ClientFactory.Initialize("http://192.168.1.135:8123/", haKey);
        var client = ClientFactory.GetClient<HistoryClient>();
        var history = await client.GetHistory(
            "climate.mysa_1a4c98_thermostat_2",
            now.AddHours(-2), now);

        var mapped = history
            .Select(MapHistoryItem);

        return mapped;
    }

    private static EfEnvironmentReading MapHistoryItem(StateObject s)
    {
        var attributes = MysaAttributes.FromDictionary(s.Attributes);

        return new EfEnvironmentReading
        {
            DesiredTemperature = attributes.Temperature,
            EntityId = s.EntityId,
            Humidity = attributes.CurrentHumidity,
            IsHeating = attributes.HvacAction == MysaActions.Heating,
            Temperature = attributes.Temperature,
            Timestamp = s.LastUpdated
        };
    }
}
