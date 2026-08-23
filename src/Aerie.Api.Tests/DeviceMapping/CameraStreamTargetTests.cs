using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Tests.DeviceMapping;

/// <summary>
/// Covers CameraStreamTarget - the pure half of CameraController. The relay
/// around it (CameraStreamRelay, the ClientWebSocket connect) stays untested,
/// the same trade HomeAssistantEventListener's socket plumbing makes.
///
/// The URL shapes asserted here were checked against a real go2rtc 1.9.14 under
/// Docker on 2026-08-23, including that a stream name may be an HA entity id
/// with dots in it.
/// </summary>
public class CameraStreamTargetTests
{
    private const string Entity = "camera.front_door_fluent";

    [Fact]
    public void TryResolve_ClusterDefault_BuildsGo2RtcWebSocketUrl()
    {
        Assert.True(CameraStreamTarget.TryResolve("http://go2rtc:1984/", Entity, out var target));

        Assert.Equal("ws://go2rtc:1984/api/ws?src=camera.front_door_fluent", target.ToString());
    }

    [Fact]
    public void TryResolve_HttpsBase_UpgradesToSecureWebSocket()
    {
        Assert.True(CameraStreamTarget.TryResolve("https://cameras.example.com/", Entity, out var target));

        Assert.Equal("wss://cameras.example.com/api/ws?src=camera.front_door_fluent", target.ToString());
    }

    /// <summary>An operator writing the address as ws:// is describing the same endpoint, so it resolves the same way rather than being rejected as "not http".</summary>
    [Theory]
    [InlineData("ws://go2rtc:1984/", "ws://go2rtc:1984/api/ws?src=camera.front_door_fluent")]
    [InlineData("wss://cameras.example.com/", "wss://cameras.example.com/api/ws?src=camera.front_door_fluent")]
    public void TryResolve_WebSocketScheme_IsAccepted(string baseAddress, string expected)
    {
        Assert.True(CameraStreamTarget.TryResolve(baseAddress, Entity, out var target));

        Assert.Equal(expected, target.ToString());
    }

    /// <summary>A hand-edited address is as likely to omit the trailing slash as to include it, and both name the same host.</summary>
    [Fact]
    public void TryResolve_BaseWithoutTrailingSlash_StillBuildsOneSlash()
    {
        Assert.True(CameraStreamTarget.TryResolve("http://go2rtc:1984", Entity, out var target));

        Assert.Equal("ws://go2rtc:1984/api/ws?src=camera.front_door_fluent", target.ToString());
    }

    /// <summary>
    /// The case Uri's own relative resolution gets wrong: combining a base whose
    /// path has no trailing slash with "api/ws" drops the last segment, turning
    /// a go2rtc mounted under /go2rtc into a request for the proxy's own root.
    /// </summary>
    [Theory]
    [InlineData("http://proxy.example.com/go2rtc")]
    [InlineData("http://proxy.example.com/go2rtc/")]
    public void TryResolve_BaseWithPathPrefix_KeepsThePrefix(string baseAddress)
    {
        Assert.True(CameraStreamTarget.TryResolve(baseAddress, Entity, out var target));

        Assert.Equal("ws://proxy.example.com/go2rtc/api/ws?src=camera.front_door_fluent", target.ToString());
    }

    /// <summary>A default port must not reappear in the authority just because the scheme changed - the address in a log line should match the one that was configured.</summary>
    [Fact]
    public void TryResolve_DefaultPort_IsNotWrittenBackIn()
    {
        Assert.True(CameraStreamTarget.TryResolve("http://go2rtc/", Entity, out var target));

        Assert.Equal("ws://go2rtc/api/ws?src=camera.front_door_fluent", target.ToString());
    }

    /// <summary>
    /// An entity id is a query *value*. Aerie never generates one containing an
    /// ampersand, but it is a string that arrives from Home Assistant and can be
    /// hand-edited in the admin UI, and one that split the query would point the
    /// relay at a different camera than the channel names.
    /// </summary>
    [Fact]
    public void TryResolve_EntityIdWithQuerySyntax_IsEscaped()
    {
        Assert.True(CameraStreamTarget.TryResolve("http://go2rtc:1984/", "camera.odd&name=x", out var target));

        Assert.Equal("ws://go2rtc:1984/api/ws?src=camera.odd%26name%3Dx", target.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("go2rtc:1984")]
    [InlineData("/api/ws")]
    [InlineData("ftp://go2rtc:1984/")]
    public void TryResolve_UnusableBaseAddress_Fails(string? baseAddress)
    {
        Assert.False(CameraStreamTarget.TryResolve(baseAddress, Entity, out var target));

        Assert.Null(target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_BlankEntityId_Fails(string? entityId)
    {
        Assert.False(CameraStreamTarget.TryResolve("http://go2rtc:1984/", entityId, out var target));

        Assert.Null(target);
    }
}
