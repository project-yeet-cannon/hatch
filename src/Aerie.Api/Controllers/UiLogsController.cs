using Aerie.Api.Common;
using Aerie.Api.Models.UiLogs;
using Aerie.Api.Services;
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
public class UiLogsController(ILogger<UiLogsController> logger, IAerieRevision revision) : ControllerBase
{
    [HttpPost]
    public IActionResult Post([FromBody] UiLogEntry[] entries)
    {
        // The actor, for the lines that have one. Deliberately the grant *id*
        // and not its label: AuthContextExtensions is explicit that a label is
        // free text an administrator typed, and free text written into a field
        // people will later filter on is a field that cannot be filtered on.
        var actorId = HttpContext.GetAuthGrant()?.Id.ToString();

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
            // {AerieRevision} is the same trick for the same reason, and it is the
            // whole of "the emitter owns the field" (docs/plans/version.md): every
            // record's `aerie_revision` means "the revision of whatever `service`
            // says produced this line". Without it these lines would carry the
            // relaying API's revision - kubernetes.container_image on this very
            // record is aerie-api's - and the one query an administrator wants,
            // "which devices are running an old bundle", would be unanswerable.
            // The relay's own revision is still worth having, one field over, so a
            // client/relay mismatch is visible rather than inferred.
            //
            // Taken from the entry rather than from the request header: the header
            // describes the batch, the entry describes itself, and every other
            // field here is the client's own claim at the same trust level. The
            // header is the fallback for a bundle predating the field.
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
                "{Service}[{SessionId}] {Message} ({Url}) :: {Metadata} :: ip={ClientIp} device={DeviceId} rev={AerieRevision} seq={AerieSequence} relay={AerieRelayRevision} actor={ActorId}",
                entry.App,
                entry.SessionId,
                entry.Message,
                entry.Url,
                entry.Metadata?.GetRawText() ?? "{}",
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                entry.DeviceId,
                ClientRevision(entry),
                ClientSequence(entry),
                revision.Revision,
                actorId);
        }

        return NoContent();
    }

    private string? ClientRevision(UiLogEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Revision)
            ? NullIfBlank(Request.Headers[AerieRevisionMiddleware.ClientHeaderName].ToString())
            : entry.Revision;

    private int? ClientSequence(UiLogEntry entry)
    {
        if (entry.Sequence is > 0) return entry.Sequence;
        return int.TryParse(Request.Headers[AerieRevisionMiddleware.ClientSequenceHeaderName].ToString(), out var parsed) && parsed > 0
            ? parsed
            : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
