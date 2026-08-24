using System.Diagnostics.CodeAnalysis;

namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// Turns a CameraFeed channel's HA entity id into the go2rtc WebSocket URL that
/// serves its video (docs/camera-devices-architecture.md). Pure, and separated
/// from CameraController for the same reason MotionEventStream is separated from
/// MotionEventsController: this is the part with rules in it, and it can be
/// tested without a socket, a camera or a database.
///
/// The entity id is doing double duty on purpose. It is already the CameraFeed
/// channel's identity - what discovery imported, what the MotionState channel is
/// grouped with - and go2rtc accepts an arbitrary string as a stream name, dots
/// included (verified against 1.9.14). So a camera needs no second identifier
/// stored anywhere: Go2RtcStreamRegistrar registers each stream under the entity
/// id it corresponds to, and this resolves the same name back. That the two
/// agree by construction is what lets go2rtc hold no camera configuration at
/// rest - it is told the name and the source together, moments before a viewer
/// asks for the name here.
/// </summary>
public static class CameraStreamTarget
{
    /// <summary>go2rtc's stream-consumer endpoint. The transport (MSE, WebRTC, MJPEG) is negotiated over the socket after it opens, not chosen here.</summary>
    private const string StreamPath = "api/ws";

    /// <summary>
    /// The upstream WebSocket URL for <paramref name="haEntityId"/>, or null if
    /// <paramref name="baseAddress"/> isn't a usable absolute http(s) URL or the
    /// entity id is blank. Null is a misconfiguration rather than a missing
    /// camera, and the caller reports it as one.
    /// </summary>
    public static bool TryResolve(string? baseAddress, string? haEntityId, [NotNullWhen(true)] out Uri? target)
    {
        target = null;

        if (string.IsNullOrWhiteSpace(baseAddress) || string.IsNullOrWhiteSpace(haEntityId))
            return false;

        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var baseUri))
            return false;

        // ws:// and wss:// are accepted as well as http(s), so a base address
        // written either way works - the scheme is normalized below regardless.
        var scheme = baseUri.Scheme switch
        {
            "http" or "ws" => "ws",
            "https" or "wss" => "wss",
            _ => null,
        };
        if (scheme is null) return false;

        // A base address may carry a path prefix (a reverse proxy mounting
        // go2rtc under a subpath), so the endpoint is appended to whatever path
        // is already there rather than replacing it. Uri's own relative
        // resolution would drop the last segment of a prefix that has no
        // trailing slash - "http://host/go2rtc" + "api/ws" resolves to
        // "http://host/api/ws" - so the slash is forced instead.
        var basePath = baseUri.AbsolutePath.TrimEnd('/');

        var builder = new UriBuilder(baseUri)
        {
            Scheme = scheme,
            Path = $"{basePath}/{StreamPath}",
            // Uri.EscapeDataString, not EscapeUriString: this is one query
            // *value*, and an entity id that somehow contained an & or a # would
            // otherwise split the query rather than being carried in it.
            Query = $"src={Uri.EscapeDataString(haEntityId)}",
        };

        // UriBuilder writes the default port back into the authority when the
        // scheme changes (http:80 -> ws:80 is no longer the default), which
        // would turn "http://go2rtc/" into "ws://go2rtc:80/". Harmless to a
        // client, ugly in a log line, and a mismatch against the address the
        // operator configured.
        if (baseUri.IsDefaultPort) builder.Port = -1;

        target = builder.Uri;
        return true;
    }
}
