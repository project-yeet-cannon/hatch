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

            // {Service} (rather than {App}) is deliberate: it's what promotes the
            // originating web app into a structured "Service" property on the JSON
            // console line (see Program.cs), which fluent-bit's service_tag.lua then
            // reads to override the container-level default `service` (aerie-api)
            // with the specific frontend app for these lines.
            //
            // {ClientIp} comes from RemoteIpAddress, which UseForwardedHeaders
            // (Program.cs) resolves from Caddy's X-Forwarded-For - trusted here
            // because `api` is only reachable from `caddy` on the isolated `edge`
            // Docker network (see docs/reverse-proxy-architecture.md), never
            // published to the host directly. {DeviceId} is client-supplied (same
            // trust level as every other field on entry) so multiple kiosks can be
            // told apart in OpenSearch even though they share IPs from the same LAN.
            logger.Log(
                level,
                "{Service}[{SessionId}] {Message} ({Url}) :: {Metadata} :: ip={ClientIp} device={DeviceId}",
                entry.App,
                entry.SessionId,
                entry.Message,
                entry.Url,
                entry.Metadata?.GetRawText() ?? "{}",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                entry.DeviceId);
        }

        return NoContent();
    }
}
