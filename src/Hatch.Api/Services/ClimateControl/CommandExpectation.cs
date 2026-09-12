using System.Globalization;
using Hatch.Api.Ef;
using Hatch.Api.Services.DeviceMapping;

namespace Hatch.Api.Services.ClimateControl;

/// <summary>What a channel should be observed carrying once a command has taken effect. Numeric and text are mutually exclusive, matching ChannelValueExtractor's split between Measurement and StateChange.</summary>
public readonly record struct ExpectedState(decimal? Numeric, string? Text)
{
    public override string ToString() =>
        Numeric?.ToString(CultureInfo.InvariantCulture) ?? Text ?? "";
}

/// <summary>
/// Pure command -> expected-observation mapping, with no DB, clock, or HA
/// client - same convention as ChannelValueExtractor and ZoneMath. Shared by
/// ClimateCommandService (which validates a command against its channel before
/// dispatch) and ReconcileCommands (which compares the expectation against what
/// was actually sampled).
/// </summary>
public static class CommandExpectation
{
    /// <summary>
    /// How far a sampled setpoint may sit from the commanded one and still
    /// count as confirmation. Nonzero because a setpoint makes a °F -> °C ->
    /// °F round trip through HA and the thermostat's own rounding, which
    /// lands up to a few tenths off what was sent. Wide enough to absorb that,
    /// narrow enough that a thermostat clamping 60°F up to its own 62°F
    /// minimum still reads as unconfirmed rather than as agreement.
    /// </summary>
    public const decimal NumericTolerance = 0.75m;

    /// <summary>The metric a command of this kind must be written against, or null for kinds with no single required metric.</summary>
    public static DeviceChannelMetric? RequiredMetric(CommandKind kind) => kind switch
    {
        CommandKind.SetPower => DeviceChannelMetric.PowerState,
        CommandKind.SetTemperature => DeviceChannelMetric.SetpointTemperature,
        CommandKind.SetHvacMode => DeviceChannelMetric.HvacMode,
        CommandKind.SetFanMode => DeviceChannelMetric.FanMode,
        CommandKind.TriggerScene => DeviceChannelMetric.Scene,
        CommandKind.PlayMedia => DeviceChannelMetric.MediaPlayback,
        _ => null,
    };

    /// <summary>
    /// Validates a command against the channel it targets, returning null when
    /// it's legal or the reason it isn't. Centralized here rather than in the
    /// controllers because the controller loop and the routine executor issue
    /// the same commands and must be held to the same rules.
    /// </summary>
    public static string? Validate(EfDeviceChannel channel, CommandKind kind, string? value)
    {
        var required = RequiredMetric(kind);
        if (required is not null && channel.Metric != required)
            return $"Channel is a {channel.Metric} channel; {kind} requires {required}.";

        // Scenes are stateless triggers, not a value a channel holds, so
        // they're the one kind that doesn't need a writable channel.
        if (kind != CommandKind.TriggerScene && channel.Direction != ChannelDirection.ReadWrite)
            return $"Channel is read-only; {kind} requires a ReadWrite channel.";

        return kind switch
        {
            CommandKind.SetPower when !bool.TryParse(value, out _) =>
                $"SetPower requires a boolean value, got '{value}'.",
            CommandKind.SetTemperature when !TryParseDecimal(value, out _) =>
                $"SetTemperature requires a decimal value, got '{value}'.",
            CommandKind.SetHvacMode or CommandKind.SetFanMode => ValidateMode(channel, value),
            CommandKind.PlayMedia when string.IsNullOrWhiteSpace(value) =>
                "PlayMedia requires a media content id.",
            _ => null,
        };
    }

    /// <summary>
    /// What the channel should read once the command lands, or null for
    /// commands whose effect isn't a channel state: a scene is a stateless
    /// trigger, and a media_player's state after PlayMedia ("playing") says
    /// nothing about whether it played the right thing. Those commands stay
    /// permanently unconfirmed, which is honest - the alternative would be
    /// confirming them against something we didn't actually verify.
    /// </summary>
    public static ExpectedState? For(CommandKind kind, string? value) => kind switch
    {
        CommandKind.SetPower => new ExpectedState(null, bool.TryParse(value, out var on) && on ? "on" : "off"),
        CommandKind.SetTemperature => TryParseDecimal(value, out var t) ? new ExpectedState(t, null) : null,
        CommandKind.SetHvacMode or CommandKind.SetFanMode =>
            string.IsNullOrWhiteSpace(value) ? null : new ExpectedState(null, value),
        _ => null,
    };

    /// <summary>Whether an observed sample carries what the command asked for.</summary>
    public static bool Matches(ExpectedState expected, ChannelLatestValue observed)
    {
        if (expected.Numeric is { } number)
            return observed.Value is { } actual && Math.Abs(actual - number) <= NumericTolerance;

        if (expected.Text is { } text)
            return observed.State is { } state && string.Equals(state, text, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    /// <summary>Renders an observed sample the same way ExpectedState renders, so EfCommand.ConfirmedValue and EfControlOverride's state columns stay comparable by eye.</summary>
    public static string Render(ChannelLatestValue observed) =>
        observed.Value?.ToString(CultureInfo.InvariantCulture) ?? observed.State ?? "";

    private static string? ValidateMode(EfDeviceChannel channel, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "A mode value is required.";

        var options = ChannelOptionsJson.Deserialize(channel.AvailableOptions);
        return options is not null && !options.Contains(value)
            ? $"'{value}' is not one of this channel's available options: {string.Join(", ", options)}"
            : null;
    }

    private static bool TryParseDecimal(string? value, out decimal parsed) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed);
}
