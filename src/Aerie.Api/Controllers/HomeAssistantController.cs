using Aerie.Api.Models.Environment;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class HomeAssistantController(TimeProvider t, IEnvironmentService envSrv) : ControllerBase
{
    [HttpGet]
    public async Task<object> Get()
    {
        return await envSrv.GetReadings();
    }

    [HttpPost]
    public async Task FetchFromHomeAssistant()
    {
        var now = t.GetUtcNow();
        var readings = await envSrv.FetchAllFromHomeAssistant("climate.", now.Subtract(TimeSpan.FromHours(2)), now);
        await envSrv.BulkInsertReadings(readings);
    }
}
