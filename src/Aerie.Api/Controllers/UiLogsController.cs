using Aerie.Api.Models.UiLogs;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>
/// Receives client-side log events from Aerie's frontend apps (dashboard/kiosk, admin, ...)
/// and writes them through the normal ILogger pipeline, so they flow into the existing
/// docker.* -> fluent-bit -> OpenSearch aerie-logs index alongside every other Aerie.Api
/// log line, visible at logs.&lt;domain&gt;. Each app is reached same-origin (e.g. from
/// kiosk.&lt;domain&gt;, see docs/reverse-proxy-architecture.md), so no CORS handling is
/// needed here.
/// </summary>
[ApiController]
[Route("api/ui-logs")]
public class UiLogsController(ILogger<UiLogsController> logger) : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] UiLogEntry[] entries)
    {
        foreach (var entry in entries)
        {
            var level = entry.Level.ToLowerInvariant() switch
            {
                "error" => LogLevel.Error,
                "warn" or "warning" => LogLevel.Warning,
                _ => LogLevel.Information,
            };

            logger.Log(
                level,
                "{App}[{SessionId}] {Message} ({Url}) :: {Metadata}",
                entry.App,
                entry.SessionId,
                entry.Message,
                entry.Url,
                entry.Metadata?.GetRawText() ?? "{}");
        }

        return NoContent();
    }
}
