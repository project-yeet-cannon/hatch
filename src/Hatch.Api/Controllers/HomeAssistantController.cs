using Hatch.Api.Common;
using Hatch.Api.Models.Environment;
using HADotNet.Core.Clients;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HomeAssistantController(
    TimeProvider t,
    IEnvironmentService envSrv
) : ControllerBase
{
    [HttpGet]
    public async Task<object> Get()
    {
        return await envSrv.GetReadings();
    }

    [RequireAdmin]
    [HttpPost]
    public async Task FetchFromHomeAssistant()
    {
        var now = t.GetUtcNow();
        var readings = await envSrv.FetchAllFromHomeAssistant("climate.", now.Subtract(TimeSpan.FromHours(2)), now);
        await envSrv.BulkInsertReadings(readings);

        readings = await envSrv.FetchAllFromHomeAssistant("sensor.h5110", now.Subtract(TimeSpan.FromHours(2)), now);
        await envSrv.BulkInsertReadings(readings);
    }

    [HttpGet("currentStates")]
    public async Task<object> CurrentStates()
    {
        var thermostats = await envSrv.FetchCurrentFromHomeAssistant("climate.").ToListAsync();
        var hygros = await envSrv.FetchCurrentFromHomeAssistant("sensor.h5110").ToListAsync();
        return new { thermostats, hygros };
    }
}
