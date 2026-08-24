using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Ef;

public enum ZoneKind { Interior, Outside }

/// <summary>
/// A real room (or the outdoors) that devices are assigned to. Replaces the
/// implicit "climate entity id that isn't excluded" notion of a zone -
/// Outside is just a Zone with Kind = Outside.
/// </summary>
[Table("Zones")]
public class EfZone
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public ZoneKind Kind { get; set; } = ZoneKind.Interior;

    public decimal? ComfortLowF { get; set; }
    public decimal? ComfortHighF { get; set; }

    /// <summary>Ascending display order on the dashboard.</summary>
    public int SortOrder { get; set; }

    /// <summary>When false, the zone is excluded from the dashboard.</summary>
    public bool Included { get; set; } = true;
}

// Same append-only rule as DeviceChannelMetric below - the column stores the
// enum's underlying int.
public enum DeviceKind { Thermostat, Hygrometer, SmartSwitch, Light, Speaker, Camera }

/// <summary>
/// A physical device mapped in from Home Assistant. May back onto one HA
/// entity (a Mysa thermostat, via several attributes) or several (a
/// hygrometer's separate temperature/humidity/battery entities) - see
/// DeviceChannel, which is what erases that distinction.
/// </summary>
[Table("Devices")]
public class EfDevice
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Optional - most control/UI behavior is driven by DeviceChannel.Metric/Direction, not Kind, so it's only worth setting where the distinction is actually meaningful (e.g. to pick a suggested channel set during Discovery import).</summary>
    public DeviceKind? Kind { get; set; }

    public Guid? ZoneId { get; set; }
    public EfZone? Zone { get; set; }

    /// <summary>Home Assistant device registry id. Null until Discovery (Phase 2) resolves it.</summary>
    public string? HaDeviceId { get; set; }

    public bool Enabled { get; set; } = true;

    public List<EfDeviceChannel> Channels { get; set; } = [];
}

// New values must be appended at the end - the column stores the enum's
// underlying int, so inserting elsewhere would silently remap every existing
// row's Metric to the wrong value.
public enum DeviceChannelMetric { Temperature, Humidity, Battery, SetpointTemperature, HvacAction, HeatingMode, PowerState, HvacMode, FanMode, Scene, MediaPlayback, CameraFeed, MotionState }

public enum ChannelDirection { Read, ReadWrite }

/// <summary>
/// One (HA entity, attribute) pair mapped to one metric on a Device. Always a
/// single field, whether a device's channels share one HA entity
/// (thermostat attributes) or point at separate ones (hygrometer entities).
/// </summary>
[Table("DeviceChannels")]
public class EfDeviceChannel
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid DeviceId { get; set; }
    public EfDevice? Device { get; set; }

    public DeviceChannelMetric Metric { get; set; }

    public required string HaEntityId { get; set; }

    /// <summary>HA attribute name to read within HaEntityId's state, e.g. "current_temperature". Null when the metric is the entity's bare state (hygrometer sensors).</summary>
    public string? HaAttribute { get; set; }

    public ChannelDirection Direction { get; set; }

    /// <summary>
    /// JSON-encoded array of legal values for a mode channel (HvacMode's
    /// "heat"/"cool"/"auto"/"off", FanMode's "auto"/"on"/"circulate"...), as
    /// last reported by HA's hvac_modes/fan_modes attributes. Null for
    /// non-mode channels and for hand-added mode channels that haven't been
    /// through discovery or a manual refresh yet.
    /// </summary>
    public string? AvailableOptions { get; set; }
}

/// <summary>
/// How to reach one camera's RTSP stream: everything go2rtc needs that Home
/// Assistant does not supply (docs/camera-devices-architecture.md).
///
/// A row per Camera device, and the reason this table exists rather than a
/// `streams:` file in the cluster: adding a camera has to be a form in the
/// admin UI, not a GitHub secret plus a workflow dispatch plus a pod restart.
/// Aerie owns the camera's address and credential; go2rtc is told about a
/// stream just before someone watches it, and holds nothing across a restart.
///
/// The password is protected (SecretProtector) and never leaves the API. The
/// username is a plain column on purpose, and the reason is the one
/// scripts/secrets/parameters.json already states about access key ids: an
/// identifier is not credential material, and keeping it readable is what
/// makes the form usable - an operator can see which account a camera is
/// configured for without being handed its password back.
/// </summary>
[Table("CameraConnections")]
public class EfCameraConnection
{
    /// <summary>The Camera device this reaches. Also the primary key - a device has one camera connection or none.</summary>
    [Key]
    public Guid DeviceId { get; set; }
    public EfDevice? Device { get; set; }

    /// <summary>
    /// Operator-set address, and the override. Null means "use what Home
    /// Assistant said" - see <see cref="DiscoveredHost"/>. A hostname is as
    /// welcome as an address; nothing here requires an IP.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// The host Home Assistant last reported for this device, from the device
    /// registry's configuration_url. Written by discovery and by a refresh,
    /// never by the operator - which is what lets it be re-read without
    /// clobbering an override, and what makes a camera that moves on DHCP
    /// follow along on its own, since HA's own integration is already
    /// tracking it.
    /// </summary>
    public string? DiscoveredHost { get; set; }

    /// <summary>RTSP port. 554 everywhere this has been seen, and a column rather than a constant because "everywhere this has been seen" is one brand.</summary>
    public int Port { get; set; } = 554;

    /// <summary>
    /// Path of the stream to pull. Defaults to Reolink's H.264 sub-stream,
    /// which is the one the kiosk should be decoding - 640x480 rather than
    /// 5MP on a wall tablet, and no transcode. Editable because the path is
    /// the most brand-specific thing here.
    /// </summary>
    public string StreamPath { get; set; } = "/h264Preview_01_sub";

    /// <summary>Camera account name. Not protected - see this type's summary.</summary>
    public string? Username { get; set; }

    /// <summary>Camera account password, protected (SecretProtector scheme v1 - obfuscation, not encryption).</summary>
    public string? PasswordProtected { get; set; }
}

/// <summary>Admin-editable scalar settings, replacing the "Dashboard" appsettings section. See SiteSettingKeys for the keys currently in use.</summary>
[Table("SiteSettings")]
public class EfSiteSetting
{
    [Key]
    public required string Key { get; set; }

    public required string Value { get; set; }
}

/// <summary>The well-known SiteSetting keys, seeded from the "Dashboard" appsettings section (see DeviceMappingSeeder).</summary>
public static class SiteSettingKeys
{
    public const string TimeZone = "TimeZone";
    public const string Latitude = "Latitude";
    public const string Longitude = "Longitude";
    public const string WeatherEntity = "WeatherEntity";
    public const string ComfortToleranceF = "ComfortToleranceF";
    public const string DefaultComfortLowF = "DefaultComfortLowF";
    public const string DefaultComfortHighF = "DefaultComfortHighF";

    /// <summary>How long the controller leaves a device alone after a manual override is detected on it, in minutes. Global for now; docs/climate-brain-architecture.md Phase 2 moves it onto EfActuatorPolicy so a compressor and a fan switch can back off for different lengths of time.</summary>
    public const string OverrideBackoffMinutes = "OverrideBackoffMinutes";

    /// <summary>Host/port/token used to connect the HADotNet client - see HomeAssistantConnectionManager.</summary>
    public const string HomeAssistantHost = "HomeAssistantHost";
    public const string HomeAssistantPort = "HomeAssistantPort";
    public const string HomeAssistantToken = "HomeAssistantToken";

    /// <summary>Absolute base URL speakers fetch media library tracks from, e.g. "https://home.example.com/media". Pairs with the MediaLibrary:RootPath appsettings value that decides what's served there - see MediaLibraryOptions.</summary>
    public const string MediaLibraryBaseUrl = "MediaLibraryBaseUrl";

    /// <summary>Wi-Fi credentials handed to the kiosk tablet's QR provisioning payload - see KioskProvisioningController.</summary>
    public const string KioskWifiSsid = "KioskWifiSsid";
    public const string KioskWifiPassword = "KioskWifiPassword";
    public const string KioskWifiSecurityType = "KioskWifiSecurityType";

    /// <summary>OAuth client credentials for the operator's own Google Cloud project, used to connect family calendars - see CalendarOAuthController. Operator-supplied rather than shipped, since Aerie redeploys to other households (docs/ethos.md).</summary>
    public const string GoogleClientId = "GoogleClientId";
    public const string GoogleClientSecret = "GoogleClientSecret";

    /// <summary>
    /// Optional exact-match override for the OAuth redirect URI. When blank it's
    /// derived from the incoming request. It exists because Google compares the
    /// redirect URI byte-for-byte against a registered value, and a proxy can
    /// rewrite what the app believes its own host is.
    /// </summary>
    public const string GoogleOAuthRedirectUri = "GoogleOAuthRedirectUri";

    /// <summary>How many days of agenda the kiosk shows, counting today. Defaults to 2 - today and tomorrow.</summary>
    public const string CalendarAgendaDays = "CalendarAgendaDays";

    /// <summary>
    /// Anthropic API key, used by the game module to write games from what a
    /// player types - see Modules/Game. Operator-supplied like the Google
    /// credentials above: it is billed to whoever runs this house, so it is a
    /// setting rather than anything shipped (docs/ethos.md). Unset simply means
    /// the game app says so instead of offering a text box.
    /// </summary>
    public const string AnthropicApiKey = "AnthropicApiKey";

    /// <summary>Which IWeatherAlertProvider supplies watches and warnings, by its Name - "nws" (default) or "none" to disable. The seam that keeps NWS's US-only reach from being a decision baked into the schema; see HazardProviders.</summary>
    public const string WeatherAlertProvider = "WeatherAlertProvider";

    /// <summary>Which IAirQualityProvider supplies AQI, by its Name - "open-meteo" (default) or "none" to disable.</summary>
    public const string AirQualityProvider = "AirQualityProvider";

    /// <summary>Contact string (email or site URL) sent in the outbound User-Agent to the weather alert provider. NWS asks callers to identify themselves and documents that anonymous traffic may be throttled or blocked.</summary>
    public const string WeatherAlertContact = "WeatherAlertContact";

    /// <summary>US AQI at or above which air quality is worth showing on the wall. Defaults to 101, the bottom of "Unhealthy for Sensitive Groups" - below that, clean air is not news.</summary>
    public const string AirQualityAlertThresholdAqi = "AirQualityAlertThresholdAqi";

    /// <summary>How far ahead, in hours, a hazard still counts as "today or the next day". Defaults to 48. Bounds both what the providers keep and what reaches the dashboard.</summary>
    public const string HazardMaxSeverityAgeHours = "HazardMaxSeverityAgeHours";
}

/// <summary>A single numeric sample from a DeviceChannel. Replaces the wide EfEnvironmentReading table - every sample is "channel X had value V at time T."</summary>
[Table("Measurements")]
[Index(nameof(ChannelId), nameof(Timestamp), IsUnique = true, Name = UniqueIndexName)]
public class EfMeasurement
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }
    public EfDeviceChannel? Channel { get; set; }

    public required DateTimeOffset Timestamp { get; set; }
    public decimal Value { get; set; }

    public const string UniqueIndexName = "IX_Measurements_ChannelId_Timestamp";
}

/// <summary>A single non-numeric sample from a DeviceChannel (e.g. hvac_action's "heating"/"idle"), for channels whose state doesn't fit Measurement's decimal column.</summary>
[Table("StateChanges")]
[Index(nameof(ChannelId), nameof(Timestamp), IsUnique = true, Name = UniqueIndexName)]
public class EfStateChange
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }
    public EfDeviceChannel? Channel { get; set; }

    public required DateTimeOffset Timestamp { get; set; }
    public required string State { get; set; }

    public const string UniqueIndexName = "IX_StateChanges_ChannelId_Timestamp";
}
