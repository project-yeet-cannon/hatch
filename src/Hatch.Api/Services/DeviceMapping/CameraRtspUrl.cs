using System.Diagnostics.CodeAnalysis;
using Hatch.Api.Common;
using Hatch.Api.Ef;

namespace Hatch.Api.Services.DeviceMapping;

/// <summary>
/// Turns a stored <see cref="EfCameraConnection"/> into the RTSP URL go2rtc
/// should pull from (docs/camera-devices-architecture.md). Pure, and separated from
/// everything that has a socket in it for the same reason
/// <see cref="CameraStreamTarget"/> is: this is the part with rules in it, and
/// the rules are about characters.
///
/// The characters are the whole job. A camera password is chosen by a person
/// in a router-grade web UI, so it contains @ and : and / far more often than
/// a password that was ever going to end up in a URL should. Unescaped, an @
/// ends the userinfo early and the URL silently points at a different host -
/// which go2rtc reports as a connection failure to an address nobody
/// configured. Both halves are percent-encoded here, once, and this is the
/// only place a credential and a URL are ever joined.
/// </summary>
public static class CameraRtspUrl
{
    /// <summary>
    /// The RTSP URL for <paramref name="connection"/>, or false when it is not
    /// configured far enough to have one - no host, from the operator or from
    /// Home Assistant.
    ///
    /// Credentials are optional: a camera with anonymous RTSP enabled is a
    /// legitimate setup, and omitting the userinfo entirely is different from
    /// sending an empty one, which some cameras reject.
    /// </summary>
    public static bool TryBuild(EfCameraConnection? connection, [NotNullWhen(true)] out string? rtspUrl)
    {
        rtspUrl = null;
        if (connection is null) return false;

        // The operator's value wins over Home Assistant's. HA is the better
        // source when it has one - its own integration tracks the camera across
        // a DHCP move - but an override exists precisely for the case where it
        // is wrong or absent, and a discovery refresh must never silently take
        // it back.
        var host = FirstNonBlank(connection.Host, connection.DiscoveredHost);
        if (host is null) return false;

        var port = connection.Port is > 0 and <= 65535 ? connection.Port : 554;

        // Stored with or without a leading slash - a form field is a form
        // field. Normalized here rather than validated on the way in, because
        // rejecting "h264Preview_01_sub" would be pedantry about a value that
        // is unambiguous.
        var path = (connection.StreamPath ?? string.Empty).TrimStart('/');

        var userInfo = BuildUserInfo(connection.Username, SecretProtector.Unprotect(connection.PasswordProtected));

        rtspUrl = $"rtsp://{userInfo}{host}:{port}/{path}";
        return true;
    }

    /// <summary>
    /// "user:pass@", "user@", or "" - percent-encoded. An empty username with
    /// a password set is treated as no credentials at all rather than as
    /// ":pass@", which is not a thing a camera accepts and would otherwise be
    /// a confusing way to report a half-filled form.
    /// </summary>
    private static string BuildUserInfo(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username)) return string.Empty;

        var user = Uri.EscapeDataString(username);
        return string.IsNullOrEmpty(password)
            ? $"{user}@"
            : $"{user}:{Uri.EscapeDataString(password)}@";
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim();
}
