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
        // Who this batch came from, for the lines that have someone.
        //
        // This path is on the gate's allow-list - a gated log endpoint means
        // the failures you most want to see are the ones that cannot report -
        // so the grant here is the one AuthMiddleware attached *without*
        // enforcing anything (AuthGate.IdentifiesWithoutEnforcing). An
        // unenrolled browser shipping logs is ordinary and all three fields are
        // simply absent on those lines.
        var grant = HttpContext.GetAuthGrant();
        var actorId = grant?.Id.ToString();

        // The person, both halves. The id is the field to filter on; the name
        // is the field to read, and it is here rather than left to a join
        // because the person doing the reading is looking at one log line in
        // OpenSearch with no way to resolve a GUID against a table.
        //
        // Which is the exact opposite of the reasoning for the *device*, one
        // field over: a grant's Label is free text an administrator typed, so
        // it stays out. A person's name went through PersonName, so it is a
        // value rather than a note - and a rename that changes what future
        // lines say while leaving past lines alone is the correct behaviour
        // for a log, which records what was true when it was written.
        var personId = grant?.PersonId?.ToString();
        var personName = grant?.Person?.Name;

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
                "{Service}[{SessionId}] {Message} ({Url}) :: {Metadata} :: ip={ClientIp} device={DeviceId} rev={AerieRevision} seq={AerieSequence} relay={AerieRelayRevision} actor={ActorId} person={PersonId} personName={PersonName}",
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
                actorId,
                personId,
                personName);
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
