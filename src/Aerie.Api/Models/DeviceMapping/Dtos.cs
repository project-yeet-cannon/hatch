using Aerie.Api.Ef;

namespace Aerie.Api.Models.DeviceMapping;

// The API-facing shapes for the Zone/Device/DeviceChannel/SiteSetting domain
// (docs/device-architecture.md). Kept separate from the Ef* entities so the
// admin app has a stable contract independent of storage details.

public record ZoneDto(Guid Id, string Name, ZoneKind Kind, decimal? ComfortLowF, decimal? ComfortHighF, int SortOrder, bool Included);

public record ZoneWriteRequest(string Name, ZoneKind Kind, decimal? ComfortLowF, decimal? ComfortHighF, int SortOrder, bool Included);

public record DeviceChannelDto(
    Guid Id, DeviceChannelMetric Metric, string HaEntityId, string? HaAttribute, ChannelDirection Direction,
    decimal? LastValue, string? LastState, DateTimeOffset? LastValueAt, IReadOnlyList<string>? AvailableOptions);

public record DeviceChannelWriteRequest(
    DeviceChannelMetric Metric, string HaEntityId, string? HaAttribute, ChannelDirection Direction, IReadOnlyList<string>? AvailableOptions = null);

/// <summary>Desired value for a HvacMode/FanMode channel (DevicesController.SetMode). Must be one of the channel's AvailableOptions when populated.</summary>
public record ChannelModeRequest(string Mode);

/// <summary>Desired setpoint for a SetpointTemperature channel (DevicesController.SetSetpoint).</summary>
public record ChannelSetpointRequest(decimal Temperature);

public record DeviceDto(Guid Id, string Name, DeviceKind? Kind, Guid? ZoneId, string? HaDeviceId, bool Enabled, IReadOnlyList<DeviceChannelDto> Channels);

/// <param name="DiscoveredHost">
/// Optional, and only Discovery sends it: the host Home Assistant reports for
/// this device. On a Camera it seeds the connection row so the stream works
/// without anyone typing an address (docs/camera-devices-architecture.md). It is
/// carried here rather than on the camera-connection write because that one is
/// the *operator's* form, and the two values are deliberately separate - see
/// EfCameraConnection.
/// </param>
public record DeviceWriteRequest(string Name, DeviceKind? Kind, Guid? ZoneId, string? HaDeviceId, bool Enabled, string? DiscoveredHost = null);

/// <summary>Time range for a BackfillChannelHistory job run (DevicesController.Backfill).</summary>
public record BackfillRequest(DateTimeOffset From, DateTimeOffset To);

/// <summary>Desired on/off state for a PowerState channel (DevicesController.SetPower).</summary>
public record ChannelPowerRequest(bool On);

/// <summary>
/// What to play on a MediaPlayback channel (DevicesController.PlayMedia).
/// MediaContentId is a path relative to the media library root
/// ("Miles Davis/Kind of Blue/01 So What.flac"), an http(s) URL, or a
/// media-source:// id - see MediaLibraryUrlResolver, which turns the first
/// into the second. MediaContentType defaults to "music".
/// </summary>
public record ChannelPlayMediaRequest(string MediaContentId, string? MediaContentType = null);

/// <summary>
/// How to reach one camera's RTSP stream, as the admin UI sees it
/// (docs/camera-devices-architecture.md).
///
/// There is no Password field, and there never will be: the API's job is to
/// let an operator *set* one, not to hand one back. <paramref name="HasPassword"/>
/// is what the form needs instead - it is the difference between an empty box
/// meaning "none set" and an empty box meaning "set, and not shown".
/// </summary>
/// <param name="Host">The operator's override, or null when Home Assistant's value is being used.</param>
/// <param name="DiscoveredHost">What Home Assistant last reported for this device. Shown as a hint under the Host field, so the override can be left empty on purpose and it is clear what that means.</param>
/// <param name="EffectiveHost">Which of the two is actually being used, resolved server-side so the form and the stream can never disagree about it.</param>
public record CameraConnectionDto(
    string? Host,
    string? DiscoveredHost,
    string? EffectiveHost,
    int Port,
    string StreamPath,
    string? Username,
    bool HasPassword);

/// <summary>
/// A write to a camera's connection settings.
/// </summary>
/// <param name="Password">
/// Three-state, and the states matter. Null leaves the stored password alone -
/// which is what a form submits when the operator edited the host and never
/// touched the password box. An empty string clears it, for a camera whose RTSP
/// is anonymous. Anything else replaces it. Without the null case, every save
/// from a form that cannot show the current value would wipe it.
/// </param>
public record CameraConnectionWriteRequest(
    string? Host,
    int? Port,
    string? StreamPath,
    string? Username,
    string? Password);

public record SiteSettingDto(string Key, string Value);

public record SiteSettingWriteRequest(string Value);

/// <summary>An HA device with no matching Aerie Device.HaDeviceId yet, with suggested name/kind/channels pre-populated for one-click import - see IDiscoveryService.</summary>
public record UnmappedHaDevice(
    string HaDeviceId,
    string SuggestedName,
    DeviceKind? SuggestedKind,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<DeviceChannelWriteRequest> SuggestedChannels,
    /// <summary>Host from the HA device registry's configuration_url, when it has one. Only a camera does anything with it - see CameraConnection - but it is carried for every device because the template that produces it is one template.</summary>
    string? DiscoveredHost = null);

/// <summary>One bucketed (averaged) numeric sample for a channel history graph.</summary>
public record ChannelHistoryPoint(DateTimeOffset Time, decimal Value);

/// <summary>One raw state-change sample for a text channel (e.g. HvacAction), rendered as a step timeline rather than a line.</summary>
public record ChannelStatePoint(DateTimeOffset Time, string State);

/// <summary>A channel's history for the admin history graph - exactly one of Points/States is populated, mirroring ChannelValueExtractor's numeric-xor-text invariant.</summary>
public record ChannelHistoryDto(
    Guid ChannelId, DeviceChannelMetric Metric, IReadOnlyList<ChannelHistoryPoint> Points, IReadOnlyList<ChannelStatePoint> States);

/// <summary>All of one device's channel histories, for the device-level history modal (small multiples sharing one time axis).</summary>
public record DeviceHistoryDto(Guid DeviceId, IReadOnlyList<ChannelHistoryDto> Channels);
