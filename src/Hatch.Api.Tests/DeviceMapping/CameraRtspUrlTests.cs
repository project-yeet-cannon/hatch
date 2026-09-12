using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Services.DeviceMapping;

namespace Hatch.Api.Tests.DeviceMapping;

/// <summary>
/// docs/camera-devices-architecture.md. These are mostly tests about characters,
/// because that is where this goes wrong: a camera password is typed into a
/// router-grade web UI by someone who has no idea it will end up between a
/// scheme and an @.
/// </summary>
public class CameraRtspUrlTests
{
    private static EfCameraConnection Connection(
        string? host = "10.0.0.9",
        string? discoveredHost = null,
        int port = 554,
        string path = "/h264Preview_01_sub",
        string? username = "admin",
        string? password = "hunter2") =>
        new()
        {
            DeviceId = Guid.NewGuid(),
            Host = host,
            DiscoveredHost = discoveredHost,
            Port = port,
            StreamPath = path,
            Username = username,
            PasswordProtected = SecretProtector.Protect(password),
        };

    [Fact]
    public void Builds_the_url_a_reolink_sub_stream_lives_at()
    {
        Assert.True(CameraRtspUrl.TryBuild(Connection(), out var url));

        Assert.Equal("rtsp://admin:hunter2@10.0.0.9:554/h264Preview_01_sub", url);
    }

    [Fact]
    public void Escapes_a_password_that_would_otherwise_end_the_userinfo()
    {
        // The failure this prevents is the nastiest one in the feature: an
        // unescaped @ ends the userinfo early, so the URL points at a host
        // nobody configured and go2rtc reports a connection failure to an
        // address that appears nowhere in the admin UI.
        Assert.True(CameraRtspUrl.TryBuild(Connection(password: "p@ss:w/rd"), out var url));

        Assert.Equal("rtsp://admin:p%40ss%3Aw%2Frd@10.0.0.9:554/h264Preview_01_sub", url);
        // One @, and it is the delimiter.
        Assert.Equal(1, url!.Count(c => c == '@'));
    }

    [Fact]
    public void Escapes_a_username_too()
    {
        Assert.True(CameraRtspUrl.TryBuild(Connection(username: "admin@home"), out var url));

        Assert.StartsWith("rtsp://admin%40home:hunter2@", url, StringComparison.Ordinal);
    }

    [Fact]
    public void The_operator_host_wins_over_what_home_assistant_reported()
    {
        // The override exists for when HA is wrong or silent, so a discovery
        // refresh writing DiscoveredHost must never take it back.
        Assert.True(CameraRtspUrl.TryBuild(Connection(host: "10.0.0.50", discoveredHost: "10.0.0.9"), out var url));

        Assert.Contains("@10.0.0.50:554/", url);
    }

    [Fact]
    public void Falls_back_to_the_host_home_assistant_reported()
    {
        Assert.True(CameraRtspUrl.TryBuild(Connection(host: null, discoveredHost: "10.0.0.9"), out var url));

        Assert.Contains("@10.0.0.9:554/", url);
    }

    [Fact]
    public void A_blank_override_is_not_a_host()
    {
        // A form field that was filled in and then cleared arrives as "", not
        // null, and treating that as a host produces "rtsp://admin:p@:554/".
        Assert.True(CameraRtspUrl.TryBuild(Connection(host: "   ", discoveredHost: "10.0.0.9"), out var url));

        Assert.Contains("@10.0.0.9:554/", url);
    }

    [Fact]
    public void No_host_anywhere_is_not_a_url()
    {
        Assert.False(CameraRtspUrl.TryBuild(Connection(host: null, discoveredHost: null), out var url));
        Assert.Null(url);
    }

    [Fact]
    public void No_connection_row_is_not_a_url()
    {
        Assert.False(CameraRtspUrl.TryBuild(null, out _));
    }

    [Fact]
    public void A_path_stored_without_a_leading_slash_still_builds_one()
    {
        Assert.True(CameraRtspUrl.TryBuild(Connection(path: "h264Preview_01_sub"), out var url));

        Assert.Equal("rtsp://admin:hunter2@10.0.0.9:554/h264Preview_01_sub", url);
    }

    [Fact]
    public void A_camera_with_no_credentials_gets_no_userinfo_at_all()
    {
        // Not ":@" and not "@" - anonymous RTSP is a real configuration, and
        // some cameras reject an empty userinfo rather than ignoring it.
        Assert.True(CameraRtspUrl.TryBuild(Connection(username: null, password: null), out var url));

        Assert.Equal("rtsp://10.0.0.9:554/h264Preview_01_sub", url);
    }

    [Fact]
    public void A_username_with_no_password_sends_just_the_username()
    {
        Assert.True(CameraRtspUrl.TryBuild(Connection(password: null), out var url));

        Assert.Equal("rtsp://admin@10.0.0.9:554/h264Preview_01_sub", url);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void An_impossible_port_falls_back_to_554(int port)
    {
        // A number field that was cleared arrives as 0. Better the RTSP default
        // than a URL that cannot connect to anything.
        Assert.True(CameraRtspUrl.TryBuild(Connection(port: port), out var url));

        Assert.Contains(":554/", url);
    }

    [Fact]
    public void A_password_that_will_not_decode_is_treated_as_absent()
    {
        // Hand-edited in the database, or written under a scheme this build
        // does not know. The camera then refuses the connection, which is a
        // better failure than throwing out of a WebSocket handshake.
        var connection = Connection();
        connection.PasswordProtected = "v9:something-else";

        Assert.True(CameraRtspUrl.TryBuild(connection, out var url));
        Assert.Equal("rtsp://admin@10.0.0.9:554/h264Preview_01_sub", url);
    }
}
