using Hatch.Api.Common;
using Hatch.Api.Models.DeviceMapping;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Controllers;

/// <summary>HA devices not yet imported as a Hatch Device, with suggested grouping/kind/channels (docs/device-architecture.md Phase 2/3).</summary>
[ApiController]
[Route("api/[controller]")]
public class DiscoveryController(IDiscoveryService discovery) : ControllerBase
{
    [RequireAdmin]
    [HttpGet("unmapped")]
    public Task<IReadOnlyList<UnmappedHaDevice>> GetUnmapped(CancellationToken ct)
        => discovery.GetUnmappedAsync(ct);
}
