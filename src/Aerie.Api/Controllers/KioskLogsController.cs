using Aerie.Api.Models.KioskLogs;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>
/// Receives client-side log events from the kiosk dashboard and writes them through the
/// normal ILogger pipeline, so they flow into the existing docker.* -> fluent-bit ->
/// OpenSearch aerie-logs index alongside every other Aerie.Api log line, visible at
/// logs.&lt;domain&gt;. Reached same-origin from kiosk.&lt;domain&gt; (see
/// docs/reverse-proxy-architecture.md), so no CORS handling is needed here.
/// </summary>
[ApiController]
[Route("api/kiosk-logs")]
public class KioskLogsController(ILogger<KioskLogsController> logger) : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] KioskLogEntry[] entries)
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
                "kiosk[{SessionId}] {Message} ({Url}) :: {Metadata}",
                entry.SessionId,
                entry.Message,
                entry.Url,
                entry.Metadata?.GetRawText() ?? "{}");
        }

        return NoContent();
    }
}
