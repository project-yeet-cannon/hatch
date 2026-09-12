using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.Hazards;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Controllers;

/// <summary>
/// The outdoor hazards the kiosk would show, on their own. The dashboard gets
/// them on GET /api/dashboard rather than here; this endpoint exists for the
/// admin app's "Active alerts" card, where an operator checks that their
/// latitude, longitude, and provider settings actually produced something -
/// an empty array on a calm, clean-air day is a pass, and the card says so.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AlertsController(IHazardService hazards) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<HazardAlert>> Get(CancellationToken ct) => hazards.GetAlertsAsync(ct);
}
