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
public class HomeAssistantController(TimeProvider time, HistoryClient haHistory) : ControllerBase
{
    [HttpGet]
    public async Task<object> Get()
    {
        var now = time.GetUtcNow();

        var history = await haHistory.GetHistory(
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
