namespace Aerie.Api.Services.Media;

/// <summary>
/// Deployment-time config for serving the music library over HTTP (the
/// "MediaLibrary" appsettings section). A Sonos speaker fetches the stream
/// itself rather than having it pushed by Home Assistant, so a file sitting on
/// an SMB share isn't playable through media_player.play_media until something
/// puts an http URL in front of it - this is that something. See
/// docs/media-library.md.
///
/// RootPath is deliberately deploy-time rather than an admin-editable
/// SiteSetting: it decides which directory the API exposes unauthenticated on
/// the LAN, which shouldn't be repointable from a web form. The runtime half -
/// the base URL speakers are told to fetch from - is the MediaLibraryBaseUrl
/// SiteSetting, since that one changes with hostnames, not with trust.
/// </summary>
public class MediaLibraryOptions
{
    public const string SectionName = "MediaLibrary";

    /// <summary>
    /// Absolute path to the library root, or empty to serve nothing (the
    /// default). Under Docker this is the in-container mount point of the
    /// share, not a UNC path - the API runs in a Linux container even though
    /// its host is Windows, so the mount has to happen in compose.
    /// </summary>
    public string RootPath { get; set; } = "";

    /// <summary>URL prefix the library is served under. The path part of MediaLibraryBaseUrl has to match it.</summary>
    public string RequestPath { get; set; } = "/media";
}
