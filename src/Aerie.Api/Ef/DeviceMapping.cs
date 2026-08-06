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
public enum DeviceKind { Thermostat, Hygrometer, SmartSwitch, Light, Speaker }

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
public enum DeviceChannelMetric { Temperature, Humidity, Battery, SetpointTemperature, HvacAction, HeatingMode, PowerState, HvacMode, FanMode, Scene, MediaPlayback }

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
