using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;

namespace Aerie.Api.Controllers;

/// <summary>
/// The kiosk end of the motion path (docs/camera-devices-architecture.md): Home
/// Assistant -> HomeAssistantEventListener -> IMotionEventDispatcher -> this
/// stream -> the dashboard app's EventSource. Server-sent events rather than a
/// WebSocket because the traffic only goes one way and EventSource reconnects
/// on its own, so a kiosk that loses the API for a minute needs no client code
/// to come back.
///
/// The stream is per replica, and so is the dispatcher behind it: `api` runs at
/// replicaCount 3 and every replica holds its own HA subscription, so whichever
/// one Traefik hands a kiosk to has the same state to report.
/// </summary>
[ApiController]
[Route("api/motion-events")]
public class MotionEventsController(IMotionEventDispatcher dispatcher) : ControllerBase
{
    /// <param name="ct">Bound to HttpContext.RequestAborted, which is what ends the sequence and unsubscribes when the kiosk goes away.</param>
    [HttpGet("stream")]
    public IResult Stream(CancellationToken ct)
    {
        // An SSE stream is a live view of right now; a cached replay of it would
        // show a kiosk motion that ended long ago.
        Response.Headers.CacheControl = "no-cache";

        return TypedResults.ServerSentEvents(Frames(ct));
    }

    /// <summary>
    /// Heartbeats go out under their own event name so a browser EventSource
    /// ignores them by default - only `message` reaches onmessage - and the
    /// client stays a one-handler affair.
    /// </summary>
    private async IAsyncEnumerable<SseItem<MotionStateChange?>> Frames([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in MotionEventStream.ReadAsync(dispatcher, ct: ct))
        {
            yield return item is { } change
                ? new SseItem<MotionStateChange?>(change)
                : new SseItem<MotionStateChange?>(null, "heartbeat");
        }
    }
}
