using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Hatch.Api.Ef;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Services.DeviceMapping;

/// <summary>
/// Holds a Home Assistant WebSocket connection open and turns motion sensor
/// state changes into motion transitions (docs/camera-devices-architecture.md).
/// HADotNet has no WebSocket client - it wraps the REST API only - so this
/// talks to /api/websocket over a raw ClientWebSocket.
///
/// Runs on every api replica, not on one elected leader. Each replica feeds its
/// own in-process motion state, and a kiosk's SSE connection lands on
/// whichever replica Traefik picked, so a replica that isn't listening is a
/// kiosk that never sees motion. The cost of the other design - one leader plus
/// a cross-replica fan-out - buys nothing here: three idle WebSocket
/// subscriptions are free, and Home Assistant is a single instance either way.
/// This is the same reasoning as HomeAssistantClientFactoryGate's "once per
/// replica, not once per deploy".
/// </summary>
public class HomeAssistantEventListener(
    IServiceScopeFactory scopes,
    IMotionEventDispatcher dispatcher,
    TimeProvider time,
    ILogger<HomeAssistantEventListener> logger) : BackgroundService
{
    /// <summary>Reconnect delay after a dropped or refused connection, doubling to MaxBackoff. Starts short because the common case is a home-network blip or an HA restart, not an outage.</summary>
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait before re-reading SiteSettings when Home Assistant isn't configured yet. A fresh install has no host/port/token until someone fills in the settings page, and this is not an error state.</summary>
    private static readonly TimeSpan UnconfiguredRetry = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the entity -> device map is trusted before it's re-read. The
    /// map is consulted only when a frame actually parses as a motion
    /// transition, so this is not on the hot path; the cost of the TTL is that
    /// a camera imported seconds ago can miss motion for up to this long. The
    /// map is also re-read on every (re)connect, so an import followed by
    /// anything that bounces the socket takes effect immediately.
    /// </summary>
    private static readonly TimeSpan ChannelMapTtl = TimeSpan.FromSeconds(60);

    /// <summary>Protocol-level ping/pong. Without a liveness check, a connection killed without a FIN - HA's host losing power, Wi-Fi dropping mid-frame - leaves ReceiveAsync blocked forever and the listener silently deaf. KeepAliveTimeout is what turns the ping into a detector: no pong inside it and the socket aborts, which surfaces here as a reconnect.</summary>
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Arbitrary; this connection only ever has the one subscription, so the id never needs to vary.</summary>
    private const int SubscribeMessageId = 1;

    /// <summary>Reused across frames rather than allocated per receive: HA sends every entity's state changes down this socket, so a per-frame 8 KB array would be steady garbage for the life of the process. Safe as a field because the session loop reads one frame at a time.</summary>
    private readonly byte[] receiveBuffer = new byte[8 * 1024];

    private IReadOnlyDictionary<string, Guid[]> motionChannels = new Dictionary<string, Guid[]>();
    private DateTimeOffset motionChannelsExpireAt = DateTimeOffset.MinValue;
    private bool loggedUnconfigured;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var backoff = InitialBackoff;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await ResolveConnectionAsync(ct) is not { } connection)
                {
                    // Logged once per process rather than once a minute: an
                    // install that hasn't been connected to HA yet shouldn't
                    // generate a log line forever.
                    if (!loggedUnconfigured)
                    {
                        logger.LogInformation("Home Assistant is not configured yet; the motion event listener is idle until it is.");
                        loggedUnconfigured = true;
                    }

                    await Task.Delay(UnconfiguredRetry, time, ct);
                    continue;
                }

                loggedUnconfigured = false;
                if (await RunSessionAsync(connection, ct))
                    backoff = InitialBackoff;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Every failure mode here - HA down, DNS gone, token rotated,
                // socket reset - is a reason to wait and try again, never a
                // reason to stop listening for the life of the process.
                logger.LogWarning(ex, "Home Assistant event listener session ended; reconnecting in {Backoff}", backoff);
            }

            try
            {
                await Task.Delay(backoff, time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            backoff = Min(backoff * 2, MaxBackoff);
        }
    }

    /// <summary>One connection, from handshake to close. Returns whether it got as far as an active subscription - a session that never authenticated must not reset the caller's backoff, or a rotated token becomes a reconnect loop at full speed.</summary>
    private async Task<bool> RunSessionAsync(HomeAssistantConnection connection, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        socket.Options.KeepAliveTimeout = KeepAliveTimeout;

        await socket.ConnectAsync(connection.WebSocketUri, ct);

        if (!await AuthenticateAsync(socket, connection.Token, ct)) return false;
        if (!await SubscribeAsync(socket, ct)) return false;

        logger.LogInformation("Subscribed to Home Assistant state_changed events at {Connection}", connection);

        // A fresh connection is the one moment we know nothing is in flight, so
        // it's the cheapest place to pick up devices imported since last time.
        motionChannelsExpireAt = DateTimeOffset.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await ReceiveFrameAsync(socket, ct) is not { } frame) break;
                if (HomeAssistantEventParser.TryReadMotionTransition(frame) is not { } transition) continue;

                await HandleTransitionAsync(transition, ct);
            }
        }
        finally
        {
            ClearMotionState();
        }

        return true;
    }

    /// <summary>
    /// Drops every device back to no-motion when the socket goes. Same
    /// reasoning as the parser's treatment of "unavailable": once we can't see,
    /// we stop claiming there is motion. Without this a device left active at
    /// the moment the connection broke stays active forever - the "off" that
    /// ended it arrives during the outage, and the next real "on" is then no
    /// change from our stale state, so no subscriber is ever told, and a kiosk
    /// modal is pinned open for good.
    /// </summary>
    private void ClearMotionState()
    {
        foreach (var deviceId in dispatcher.ActiveDeviceIds)
            dispatcher.SetMotionState(deviceId, false);
    }

    /// <summary>HA's handshake: the server opens with auth_required, the client answers with the long-lived access token, and the server replies auth_ok or auth_invalid.</summary>
    private async Task<bool> AuthenticateAsync(ClientWebSocket socket, string token, CancellationToken ct)
    {
        var greeting = await ReceiveFrameAsync(socket, ct);
        var greetingType = greeting is null ? null : HomeAssistantEventParser.ReadMessageType(greeting);
        if (greetingType != HomeAssistantEventParser.AuthRequired)
        {
            logger.LogWarning("Home Assistant did not open with {Expected}; got {Actual}",
                HomeAssistantEventParser.AuthRequired,
                greetingType ?? (greeting is null ? "a closed socket" : "an unreadable frame"));
            return false;
        }

        await SendAsync(socket, new { type = "auth", access_token = token }, ct);

        var reply = await ReceiveFrameAsync(socket, ct);
        var replyType = reply is null ? null : HomeAssistantEventParser.ReadMessageType(reply);
        if (replyType == HomeAssistantEventParser.AuthOk) return true;

        // Called out separately because it is the one failure here a person has
        // to fix: reconnecting will not repair a token that HA has revoked.
        if (replyType == HomeAssistantEventParser.AuthInvalid)
            logger.LogError("Home Assistant rejected the access token. Re-issue a long-lived token and update it in Settings.");
        else
            logger.LogWarning("Unexpected reply to the Home Assistant auth frame: {Type}", replyType ?? "none");

        return false;
    }

    /// <summary>
    /// Subscribes to every state_changed event and filters client-side against
    /// the MotionState channel map. HA can filter server-side via
    /// subscribe_trigger with an entity list, but that list would have to be
    /// re-sent - and the subscription torn down and rebuilt - every time a
    /// camera is imported or removed. state_changed on a house-sized instance
    /// is a few frames a second, so the parse cost of filtering here is well
    /// under the cost of keeping a server-side filter honest.
    /// </summary>
    private async Task<bool> SubscribeAsync(ClientWebSocket socket, CancellationToken ct)
    {
        await SendAsync(socket, new { id = SubscribeMessageId, type = "subscribe_events", event_type = "state_changed" }, ct);

        while (true)
        {
            if (await ReceiveFrameAsync(socket, ct) is not { } frame)
            {
                logger.LogWarning("Home Assistant closed the socket before confirming the state_changed subscription");
                return false;
            }

            // Nothing else is subscribed on this connection, so the first
            // result frame is necessarily this subscription's.
            if (HomeAssistantEventParser.ReadMessageType(frame) != HomeAssistantEventParser.Result) continue;

            using var doc = JsonDocument.Parse(frame);
            if (doc.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
                return true;

            logger.LogWarning("Home Assistant refused the state_changed subscription: {Frame}", frame);
            return false;
        }
    }

    /// <summary>Resolves the entity to the devices that own it and hands each one to the dispatcher, which is where everything Home-Assistant-shaped stops (docs/camera-devices-architecture.md).</summary>
    private async Task HandleTransitionAsync(MotionTransition transition, CancellationToken ct)
    {
        var channels = await GetMotionChannelsAsync(ct);
        if (!channels.TryGetValue(transition.EntityId, out var deviceIds)) return;

        foreach (var deviceId in deviceIds)
        {
            logger.LogInformation("Motion {State} on device {DeviceId} (entity {EntityId})",
                transition.IsActive ? "started" : "ended", deviceId, transition.EntityId);

            dispatcher.SetMotionState(deviceId, transition.IsActive);
        }
    }

    /// <summary>Motion entity id -> the devices whose MotionState channel points at it. An array rather than a single id because nothing stops two devices from being mapped to one sensor, and picking one of them arbitrarily would drop the other's modal.</summary>
    private async Task<IReadOnlyDictionary<string, Guid[]>> GetMotionChannelsAsync(CancellationToken ct)
    {
        if (time.GetUtcNow() < motionChannelsExpireAt) return motionChannels;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var rows = await db.DeviceChannels.AsNoTracking()
            .Where(c => c.Metric == DeviceChannelMetric.MotionState && c.Device!.Enabled)
            .Select(c => new { c.HaEntityId, c.DeviceId })
            .ToListAsync(ct);

        motionChannels = rows
            .GroupBy(r => r.HaEntityId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r.DeviceId).ToArray(), StringComparer.Ordinal);
        motionChannelsExpireAt = time.GetUtcNow() + ChannelMapTtl;

        return motionChannels;
    }

    /// <summary>The connection manager is scoped (it holds a DbContext factory behind an interface the request pipeline also uses), so a singleton BackgroundService has to open its own scope rather than inject it.</summary>
    private async Task<HomeAssistantConnection?> ResolveConnectionAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IHomeAssistantConnectionManager>()
            .ResolveAsync(ct);
    }

    private static Task SendAsync(ClientWebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, endOfMessage: true, ct);

    /// <summary>One whole text message, reassembled across fragments, or null once the peer has closed. state_changed frames carry the entity's full attribute set, so a single read is not enough - a camera's attributes alone can exceed any fixed buffer.</summary>
    private async Task<string?> ReceiveFrameAsync(ClientWebSocket socket, CancellationToken ct)
    {
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(receiveBuffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            message.Write(receiveBuffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
