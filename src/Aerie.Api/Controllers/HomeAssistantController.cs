using Aerie.Api.Models.Environment;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class HomeAssistantController(IEnvironmentService envSrv) : ControllerBase
{
    [HttpGet]
    public async Task<object> Get()
    {
        return await envSrv.GetReadings();
    }

    [HttpPost]
    public async Task FetchFromHomeAssistant()
    {
        var readings = await envSrv.FetchAllFromHomeAssistant("climate.");
        await envSrv.BulkInsertReadings(readings);
    }
}
