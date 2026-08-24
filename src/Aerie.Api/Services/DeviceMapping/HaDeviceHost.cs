namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// The host out of a Home Assistant device's configuration_url
/// (docs/plans/cameras.md Phase 11).
///
/// This is the small piece of machinery that lets a camera stop being told a
/// static address. HA's device registry records where a device's own web UI
/// lives - for a Reolink, `http://192.168.1.40` - and the integration keeps
/// that current when the camera moves on DHCP, because it has to in order to
/// keep talking to it. Aerie reads it during discovery, so the address in
/// CameraConnections is a thing HA already tracks rather than a thing an
/// operator has to remember to update.
///
/// What comes back is a host, never a URL: it is going into an RTSP URL on a
/// different port with a different scheme, so the only useful part is the
/// authority's host. The port is deliberately dropped for the same reason -
/// a configuration_url of `http://10.0.0.9:8000` says where the web UI is, not
/// where RTSP is.
/// </summary>
public static class HaDeviceHost
{
    /// <summary>
    /// The host in <paramref name="configurationUrl"/>, or null when there
    /// isn't one to find. Null is the ordinary case, not an error: most
    /// integrations set no configuration_url at all, and some set a
    /// `homeassistant://` link to a config page rather than an address of
    /// anything.
    /// </summary>
    public static string? FromConfigurationUrl(string? configurationUrl)
    {
        if (string.IsNullOrWhiteSpace(configurationUrl)) return null;

        if (!Uri.TryCreate(configurationUrl.Trim(), UriKind.Absolute, out var uri)) return null;

        // http(s) only. A `homeassistant://` URL points at a page inside HA
        // itself, and its "host" is a config-flow handler name - which would
        // otherwise be stored as a camera's address and fail to resolve much
        // later, somewhere far from here.
        if (uri.Scheme is not ("http" or "https")) return null;

        var host = uri.Host;
        if (string.IsNullOrWhiteSpace(host)) return null;

        // An IPv6 literal has to keep its brackets, because something is about
        // to append ":554" to this. .NET's Uri.Host already includes them -
        // checked rather than assumed, since adding a second pair produces
        // "[[fd00::1]]", which parses as neither an address nor an error.
        if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('['))
            return $"[{host}]";

        return host;
    }
}
