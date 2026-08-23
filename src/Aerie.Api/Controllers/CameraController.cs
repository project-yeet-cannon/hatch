using System.Net.WebSockets;
using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Controllers;

/// <summary>
/// The video half of the camera path (docs/plans/cameras.md Phase 7), and the
/// other end of the sequence MotionEventsController starts: motion on a camera
/// reaches the kiosk as a device id over SSE, and the kiosk opens this socket
/// for that device id to see what tripped it.
///
/// Video comes from go2rtc, not from Home Assistant. HA bundles go2rtc but binds
/// its API to a port it does not expose, documented as debug-only; a standalone
/// instance (charts/aerie's go2rtc Deployment) serves the same RTSP sub-stream
/// over a documented, stable endpoint instead. HA stays the source of the
/// camera's *identity* - the CameraFeed channel's HaEntityId, which is also the
/// go2rtc stream name (see CameraStreamTarget).
///
/// This is a relay rather than a redirect on purpose. A kiosk holds one
/// connection to one origin, gets there through the same auth as every other
/// Aerie request, and never learns a camera's address or password - which are
/// the only things that would let it be watched from anywhere else.
/// </summary>
[ApiController]
[Route("api/devices")]
public class CameraController(
    AerieContext db, IOptions<CameraStreamOptions> options, ILogger<CameraController> logger
) : ControllerBase
{
    /// <summary>
    /// Protocol-level ping/pong on the upstream socket, matching
    /// HomeAssistantEventListener's reasoning: a go2rtc pod killed without a FIN
    /// leaves ReceiveAsync blocked forever, and a relay parked there holds a
    /// camera connection open for a viewer who is no longer being sent anything.
    /// </summary>
    private static readonly TimeSpan UpstreamKeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpstreamKeepAliveTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// WebSocket endpoint. Returns Task rather than an IActionResult because a
    /// successful call hijacks the response and never produces one; the failure
    /// paths set a status code and return, which is what a client that opened a
    /// WebSocket sees as a failed handshake.
    /// </summary>
    /// <param name="ct">HttpContext.RequestAborted - the kiosk closing the tab, navigating away, or losing power.</param>
    [HttpGet("{deviceId:guid}/camera/stream")]
    public async Task Stream(Guid deviceId, CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            // The endpoint is only ever reachable as an upgrade, so a plain GET
            // is a caller mistake rather than a server one. Says so in the body
            // because a bare 400 on a URL that works in the app is otherwise a
            // long afternoon.
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("This endpoint requires a WebSocket upgrade.", ct);
            return;
        }

        // Enabled devices only, matching what HomeAssistantEventListener will
        // raise motion for - a disabled camera can't open the modal, so it
        // shouldn't answer for a hand-typed URL either.
        var entityId = await db.DeviceChannels.AsNoTracking()
            .Where(c => c.DeviceId == deviceId
                && c.Metric == DeviceChannelMetric.CameraFeed
                && c.Device!.Enabled)
            .Select(c => c.HaEntityId)
            .FirstOrDefaultAsync(ct);

        if (entityId is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!CameraStreamTarget.TryResolve(options.Value.Go2RtcBaseAddress, entityId, out var target))
        {
            // Not the caller's fault and not a missing camera: the configured
            // go2rtc address is unusable, which no retry fixes.
            logger.LogError(
                "Cameras:Go2RtcBaseAddress is not a usable http(s) address: {BaseAddress}",
                options.Value.Go2RtcBaseAddress);
            Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        // Upstream first, then accept the client - the order matters. Accepting
        // first would complete the handshake, and every failure below would then
        // reach the browser as a socket that opened and immediately closed, with
        // the reason on the server only. Connecting first keeps those failures
        // on the HTTP response, where a status code says which one it was.
        using var upstream = new ClientWebSocket();
        upstream.Options.KeepAliveInterval = UpstreamKeepAliveInterval;
        upstream.Options.KeepAliveTimeout = UpstreamKeepAliveTimeout;

        try
        {
            await upstream.ConnectAsync(target, ct);
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
        {
            // go2rtc is down or unreachable. 502 rather than 500: the camera
            // path is configured correctly and something behind it is not.
            // Logged with the entity id, not the full URL - the URL is where a
            // future go2rtc auth credential would end up.
            logger.LogWarning(ex, "Camera stream upstream unreachable for {EntityId}", entityId);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        catch (OperationCanceledException)
        {
            // The kiosk gave up during the connect. Nothing to report.
            return;
        }

        using var client = await HttpContext.WebSockets.AcceptWebSocketAsync();

        logger.LogInformation("Camera stream opened for device {DeviceId} ({EntityId})", deviceId, entityId);
        try
        {
            await CameraStreamRelay.RelayAsync(client, upstream, ct);
        }
        finally
        {
            logger.LogInformation("Camera stream closed for device {DeviceId} ({EntityId})", deviceId, entityId);
        }
    }
}
