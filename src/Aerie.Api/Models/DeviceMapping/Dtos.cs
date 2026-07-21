using Aerie.Api.Ef;

namespace Aerie.Api.Models.DeviceMapping;

// The API-facing shapes for the Zone/Device/DeviceChannel/SiteSetting domain
// (docs/device-architecture.md). Kept separate from the Ef* entities so the
// admin app has a stable contract independent of storage details.

public record ZoneDto(Guid Id, string Name, ZoneKind Kind, decimal? ComfortLowF, decimal? ComfortHighF, int SortOrder, bool Included);

public record ZoneWriteRequest(string Name, ZoneKind Kind, decimal? ComfortLowF, decimal? ComfortHighF, int SortOrder, bool Included);

public record DeviceChannelDto(Guid Id, DeviceChannelMetric Metric, string HaEntityId, string? HaAttribute, ChannelDirection Direction);

public record DeviceChannelWriteRequest(DeviceChannelMetric Metric, string HaEntityId, string? HaAttribute, ChannelDirection Direction);

public record DeviceDto(Guid Id, string Name, DeviceKind Kind, Guid? ZoneId, string? HaDeviceId, bool Enabled, IReadOnlyList<DeviceChannelDto> Channels);

public record DeviceWriteRequest(string Name, DeviceKind Kind, Guid? ZoneId, string? HaDeviceId, bool Enabled);

/// <summary>Time range for a BackfillChannelHistory job run (DevicesController.Backfill).</summary>
public record BackfillRequest(DateTimeOffset From, DateTimeOffset To);

public record SiteSettingDto(string Key, string Value);

public record SiteSettingWriteRequest(string Value);

/// <summary>An HA device with no matching Aerie Device.HaDeviceId yet, with suggested name/kind/channels pre-populated for one-click import - see IDiscoveryService.</summary>
public record UnmappedHaDevice(
    string HaDeviceId,
    string SuggestedName,
    DeviceKind? SuggestedKind,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<DeviceChannelWriteRequest> SuggestedChannels);
