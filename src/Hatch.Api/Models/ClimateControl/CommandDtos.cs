using Hatch.Api.Ef;

namespace Hatch.Api.Models.ClimateControl;

/// <summary>One ledger row, flattened with the channel/device context needed to read it without a second lookup.</summary>
public record CommandDto(
    Guid Id,
    Guid ChannelId,
    Guid? DeviceId,
    string? DeviceName,
    string HaEntityId,
    DeviceChannelMetric Metric,
    CommandKind Kind,
    string? Value,
    CommandSource Source,
    string? Reason,
    Guid? DecisionId,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DispatchedAt,
    CommandOutcome Outcome,
    string? Error,
    DateTimeOffset? ConfirmedAt,
    string? ConfirmedValue,
    DateTimeOffset? OverriddenAt);

/// <summary>One detected manual override, with the command whose effect was undone.</summary>
public record ControlOverrideDto(
    Guid Id,
    Guid DeviceId,
    string? DeviceName,
    Guid ChannelId,
    string HaEntityId,
    Guid CommandId,
    DateTimeOffset DetectedAt,
    string? ExpectedState,
    string? ObservedState,
    DateTimeOffset SuppressedUntil,
    bool IsActive);
