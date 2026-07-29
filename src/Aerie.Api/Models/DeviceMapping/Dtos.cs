using Aerie.Api.Ef;

namespace Aerie.Api.Models.DeviceMapping;

// The API-facing shapes for the Zone/Device/DeviceChannel/SiteSetting domain
// (docs/device-architecture.md). Kept separate from the Ef* entities so the
// admin app has a stable contract independent of storage details.

public record ZoneDto(Guid Id, string Name, ZoneKind Kind, decimal? ComfortLowF, decimal? ComfortHighF, int SortOrder, bool Included);

public record ZoneWriteRequest(string Name, ZoneKind Kind, decimal? ComfortLowF, decimal? ComfortHighF, int SortOrder, bool Included);

public record DeviceChannelDto(
    Guid Id, DeviceChannelMetric Metric, string HaEntityId, string? HaAttribute, ChannelDirection Direction,
    decimal? LastValue, string? LastState, DateTimeOffset? LastValueAt);

public record DeviceChannelWriteRequest(DeviceChannelMetric Metric, string HaEntityId, string? HaAttribute, ChannelDirection Direction);

public record DeviceDto(Guid Id, string Name, DeviceKind Kind, Guid? ZoneId, string? HaDeviceId, bool Enabled, IReadOnlyList<DeviceChannelDto> Channels);

public record DeviceWriteRequest(string Name, DeviceKind Kind, Guid? ZoneId, string? HaDeviceId, bool Enabled);

/// <summary>Time range for a BackfillChannelHistory job run (DevicesController.Backfill).</summary>
public record BackfillRequest(DateTimeOffset From, DateTimeOffset To);

/// <summary>Desired on/off state for a PowerState channel (DevicesController.SetPower).</summary>
public record ChannelPowerRequest(bool On);

public record SiteSettingDto(string Key, string Value);

public record SiteSettingWriteRequest(string Value);

/// <summary>An HA device with no matching Aerie Device.HaDeviceId yet, with suggested name/kind/channels pre-populated for one-click import - see IDiscoveryService.</summary>
public record UnmappedHaDevice(
    string HaDeviceId,
    string SuggestedName,
    DeviceKind? SuggestedKind,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<DeviceChannelWriteRequest> SuggestedChannels);

/// <summary>One bucketed (averaged) numeric sample for a channel history graph.</summary>
public record ChannelHistoryPoint(DateTimeOffset Time, decimal Value);

/// <summary>One raw state-change sample for a text channel (e.g. HvacAction), rendered as a step timeline rather than a line.</summary>
public record ChannelStatePoint(DateTimeOffset Time, string State);

/// <summary>A channel's history for the admin history graph - exactly one of Points/States is populated, mirroring ChannelValueExtractor's numeric-xor-text invariant.</summary>
public record ChannelHistoryDto(
    Guid ChannelId, DeviceChannelMetric Metric, IReadOnlyList<ChannelHistoryPoint> Points, IReadOnlyList<ChannelStatePoint> States);

/// <summary>All of one device's channel histories, for the device-level history modal (small multiples sharing one time axis).</summary>
public record DeviceHistoryDto(Guid DeviceId, IReadOnlyList<ChannelHistoryDto> Channels);
