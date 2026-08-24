namespace Aerie.Api.Services.DeviceMapping;

/// <summary>
/// Deployment-time config for the camera video path (the "Cameras" appsettings
/// section) - see docs/camera-devices-architecture.md.
///
/// Deploy-time rather than an admin-editable SiteSetting, for the same reason
/// MediaLibraryOptions.RootPath is: this names the host the API will open a
/// WebSocket to and relay bytes from to every kiosk in the house. That is a
/// topology fact, fixed by how Aerie is installed, and it should not be
/// repointable from a web form. The parts that do vary per household - which
/// cameras exist and where they live on the network - are not here either;
/// they are go2rtc's own configuration, which is how a camera password stays
/// out of both Aerie's database and its git history.
/// </summary>
public class CameraStreamOptions
{
    public const string SectionName = "Cameras";

    /// <summary>
    /// Base address of the go2rtc instance serving camera video. The default
    /// resolves in the cluster because charts/aerie names that Service "go2rtc"
    /// - the same arrangement as KioskFiles:BaseAddress and the `files` Service,
    /// and overridable here for the same reason: a rename on either side should
    /// be a config change, not a silent failure to connect.
    /// </summary>
    public string Go2RtcBaseAddress { get; set; } = "http://go2rtc:1984/";
}
