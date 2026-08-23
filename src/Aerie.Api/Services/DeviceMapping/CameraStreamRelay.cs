using System.Net.WebSockets;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// Pumps bytes between a kiosk's WebSocket and go2rtc's, in both directions,
/// until either end goes away (docs/plans/cameras.md Phase 7).
///
/// Deliberately knows nothing about what it is carrying. go2rtc's stream socket
/// is a small JSON control protocol followed by binary fragmented MP4 - the
/// client asks for a transport with {"type":"mse","value":"&lt;codec list&gt;"},
/// go2rtc answers with the exact MIME string to hand MediaSource, and then
/// sends an ftyp+moov init segment and moof+mdat fragments forever. Every part
/// of that is between the dashboard and go2rtc. Teaching this relay the protocol
/// would buy nothing and would put a second implementation of it in the path of
/// every frame, to be updated whenever go2rtc's changes.
///
/// What it exists for instead: the kiosk gets one connection, to one origin,
/// authenticated the same way every other Aerie request is, and never learns a
/// camera's address or password.
/// </summary>
public static class CameraStreamRelay
{
    /// <summary>
    /// Sized against what go2rtc actually sends: the init segment measured ~675
    /// bytes and fragments ran to ~13KB on a 1080p test stream. A message larger
    /// than this is not lost - it arrives as several ReceiveAsync results and is
    /// forwarded as the same several frames, with EndOfMessage preserved, so the
    /// far end reassembles exactly one message either way. The buffer is
    /// therefore a throughput knob, not a correctness one, and stays small
    /// because there is one of it per viewer per direction.
    /// </summary>
    private const int BufferSize = 16 * 1024;

    /// <summary>
    /// Returns once both directions have finished. Neither socket is disposed
    /// here - the caller owns both, and ASP.NET owns the response one in
    /// particular.
    /// </summary>
    public static async Task RelayAsync(WebSocket client, WebSocket upstream, CancellationToken ct)
    {
        // Either direction ending ends the call. A camera stream is not
        // half-duplex-useful: an upstream that closed has no more video to send,
        // and a kiosk that walked away is nobody to send it to, so the first
        // completion cancels its opposite rather than leaving a pump parked in
        // ReceiveAsync holding a go2rtc connection - and therefore a camera
        // connection - open.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var toUpstream = PumpAsync(client, upstream, stop);
        var toClient = PumpAsync(upstream, client, stop);

        await Task.WhenAll(toUpstream, toClient);
    }

    /// <summary>
    /// Forwards frames one way. Each direction writes to exactly one socket and
    /// no other task writes to that socket, which is what makes this safe
    /// without a lock - WebSocket.SendAsync permits only one send in flight.
    /// </summary>
    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationTokenSource stop)
    {
        var buffer = new byte[BufferSize];

        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await from.ReceiveAsync(buffer, stop.Token);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    // Pass the close through rather than inventing one, so a
                    // go2rtc that rejected the stream (an unknown src name, a
                    // codec it can't produce) reaches the browser as that
                    // status and reason instead of a generic hang-up.
                    if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await to.CloseOutputAsync(
                            received.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                            received.CloseStatusDescription,
                            CancellationToken.None);
                    }
                    return;
                }

                // MessageType and EndOfMessage both forwarded as received. The
                // first matters because go2rtc's control replies are Text and
                // its video is Binary, and a browser delivers them to different
                // branches of onmessage; the second is what keeps a fragment
                // that spilled over the buffer a single message on arrival.
                await to.SendAsync(
                    buffer.AsMemory(0, received.Count),
                    received.MessageType,
                    received.EndOfMessage,
                    stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The other direction finished, or the request was aborted. Both are
            // the normal way a viewer stops watching.
        }
        catch (WebSocketException)
        {
            // A kiosk that lost power or a go2rtc that restarted. Nothing to do
            // but stop; the finally below takes the other direction with it.
        }
        finally
        {
            // Whichever pump ends first releases the other from ReceiveAsync.
            await stop.CancelAsync();
        }
    }
}
