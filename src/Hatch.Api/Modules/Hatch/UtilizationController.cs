using Hatch.Api.Common;
using Hatch.Api.Ef;
using Microsoft.AspNetCore.Mvc;

namespace Hatch.Api.Modules.Hatch;

/// <summary>
/// The account's own Claude headroom, proxied.
///
/// Read by the server and never by the browser: the subscription token is a
/// credential, and a page that held one would be a page that leaked one. What
/// crosses the wire is Hatch's own vocabulary - <c>window</c>, <c>label</c>,
/// <c>tone</c> - already reshaped by <see cref="ClaudeUsageClient"/>, so the
/// day Anthropic renames a field there is one file to fix and no page that has
/// gone blank.
///
/// <c>204 No Content</c> when no token is configured. That is the important
/// answer here and it is not an error: an installation with no Claude
/// subscription gets a Hatch with no battery, no errors, and no empty box where
/// a battery should be. This is an enhancement to a tracker, not a dependency
/// of one.
/// </summary>
[ApiController]
[Route("api/hatch/utilization")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class UtilizationController(UtilizationCache cache) : ControllerBase
{
    /// <summary><paramref name="refresh"/> is the modal's refresh control: it bypasses both the freshness window and the failure backoff, because it is one person asking once.</summary>
    [HttpGet]
    public async Task<ActionResult<UtilizationReading>> Get([FromQuery] bool refresh, CancellationToken ct)
    {
        var reading = await cache.ReadAsync(refresh, ct);
        return reading is null ? NoContent() : reading;
    }
}
