using Aerie.Api.Models.DeviceMapping;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>HA devices not yet imported as an Aerie Device, with suggested grouping/kind/channels (docs/device-architecture.md Phase 2/3).</summary>
[ApiController]
[Route("api/[controller]")]
public class DiscoveryController(IDiscoveryService discovery) : ControllerBase
{
    [HttpGet("unmapped")]
    public Task<IReadOnlyList<UnmappedHaDevice>> GetUnmapped(CancellationToken ct)
        => discovery.GetUnmappedAsync(ct);
}
