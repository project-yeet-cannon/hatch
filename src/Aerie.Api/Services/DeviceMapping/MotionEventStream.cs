using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// One kiosk's view of motion, as an async sequence: the devices already in
/// motion when it connected, then every change for as long as it stays
/// connected (docs/plans/cameras.md Phase 6). Separate from
/// MotionEventsController so the part with the ordering rules in it can be
/// tested without an HTTP connection; the controller only wraps this in SSE
/// framing.
/// </summary>
public static class MotionEventStream
{
    /// <summary>
    /// How long the sequence waits for a change before yielding a heartbeat.
    /// A kiosk that loses power (or drops off the Wi-Fi) leaves a request that
    /// is never aborted and a handler that is never unsubscribed - nothing
    /// notices until something is written down the connection and the write
    /// fails. Motion can be quiet for days, so the heartbeat is what bounds how
    /// long a dead kiosk keeps its subscription, and it also keeps a proxy from
    /// closing the stream for being idle. Same reasoning as the WebSocket
    /// keepalive in HomeAssistantEventListener.
    /// </summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs until <paramref name="ct"/> fires - the connection going away -
    /// and unsubscribes on the way out however it ends. A null item is a
    /// heartbeat; anything else is a change to send.
    /// </summary>
    public static async IAsyncEnumerable<MotionStateChange?> ReadAsync(
        IMotionEventDispatcher dispatcher,
        TimeSpan? heartbeatInterval = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The dispatcher raises on the WebSocket listener's receive loop, and
        // handlers there must not block, so the handler only hands the change
        // to this queue. AllowSynchronousContinuations stays off (the default,
        // named here because it is load-bearing): a synchronous continuation
        // would run this sequence's consumer - writing to a possibly-stalled
        // response - on the listener's thread, stalling motion for every device.
        var queue = Channel.CreateUnbounded<MotionStateChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

        void OnChanged(MotionStateChange change) => queue.Writer.TryWrite(change);

        dispatcher.Changed += OnChanged;
        try
        {
            // Subscribe first, read the snapshot second: a change that lands
            // between the two is queued behind the snapshot rather than lost.
            // The cost is that it can repeat a device the snapshot already
            // reported, which is harmless - a change carries absolute state,
            // not a toggle. Both happen on this first MoveNextAsync, before
            // anything is yielded, so the window is as small as it can be.
            var snapshot = dispatcher.ActiveDeviceIds;

            foreach (var deviceId in snapshot)
                yield return new MotionStateChange(deviceId, true);

            // Nothing is written down an SSE connection until its first frame,
            // response headers included. With no motion to report there is no
            // frame, so a kiosk connecting on a quiet night would sit with an
            // EventSource that has not opened yet - indistinguishable from a
            // broken API - until the first heartbeat. This is that heartbeat,
            // up front.
            yield return null;

            while (!ct.IsCancellationRequested)
            {
                var next = await NextAsync(queue.Reader, heartbeatInterval ?? HeartbeatInterval, ct);

                // A heartbeat and a cancelled wait both come back as null; only
                // the token says which, and a cancelled one ends the sequence
                // rather than writing a last frame nobody is there to read.
                if (ct.IsCancellationRequested) yield break;

                yield return next;
            }
        }
        finally
        {
            dispatcher.Changed -= OnChanged;
        }
    }

    /// <summary>The next queued change, or null once <paramref name="heartbeatInterval"/> passes without one.</summary>
    private static async ValueTask<MotionStateChange?> NextAsync(
        ChannelReader<MotionStateChange> reader,
        TimeSpan heartbeatInterval,
        CancellationToken ct)
    {
        if (reader.TryRead(out var queued)) return queued;

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(heartbeatInterval);

        try
        {
            return await reader.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
