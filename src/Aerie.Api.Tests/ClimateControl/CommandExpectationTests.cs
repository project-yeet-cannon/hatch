using Aerie.Api.Ef;
using Aerie.Api.Services.ClimateControl;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Tests.ClimateControl;

/// <summary>Covers the pure command -> expected-observation mapping shared by ClimateCommandService (pre-dispatch validation) and ReconcileCommands (post-dispatch comparison).</summary>
public class CommandExpectationTests
{
    private static EfDeviceChannel Channel(
        DeviceChannelMetric metric, ChannelDirection direction = ChannelDirection.ReadWrite, IReadOnlyList<string>? options = null) =>
        new()
        {
            HaEntityId = "climate.test",
            Metric = metric,
            Direction = direction,
            AvailableOptions = ChannelOptionsJson.Serialize(options),
        };

    [Theory]
    [InlineData(CommandKind.SetPower, DeviceChannelMetric.PowerState)]
    [InlineData(CommandKind.SetTemperature, DeviceChannelMetric.SetpointTemperature)]
    [InlineData(CommandKind.SetHvacMode, DeviceChannelMetric.HvacMode)]
    [InlineData(CommandKind.SetFanMode, DeviceChannelMetric.FanMode)]
    [InlineData(CommandKind.TriggerScene, DeviceChannelMetric.Scene)]
    [InlineData(CommandKind.PlayMedia, DeviceChannelMetric.MediaPlayback)]
    public void RequiredMetric_PairsEveryKindWithItsMetric(CommandKind kind, DeviceChannelMetric expected) =>
        Assert.Equal(expected, CommandExpectation.RequiredMetric(kind));

    [Fact]
    public void Validate_AcceptsAWellFormedCommand() =>
        Assert.Null(CommandExpectation.Validate(Channel(DeviceChannelMetric.SetpointTemperature), CommandKind.SetTemperature, "72.5"));

    [Fact]
    public void Validate_RejectsAKindTheChannelDoesNotCarry() =>
        Assert.NotNull(CommandExpectation.Validate(Channel(DeviceChannelMetric.Humidity), CommandKind.SetTemperature, "72"));

    [Fact]
    public void Validate_RejectsAWriteToAReadOnlyChannel() =>
        Assert.NotNull(CommandExpectation.Validate(
            Channel(DeviceChannelMetric.PowerState, ChannelDirection.Read), CommandKind.SetPower, "true"));

    [Fact]
    public void Validate_AllowsSceneTriggerOnAReadOnlyChannel() =>
        // A scene isn't a value the channel holds, so it's the one kind that
        // doesn't need a writable channel.
        Assert.Null(CommandExpectation.Validate(
            Channel(DeviceChannelMetric.Scene, ChannelDirection.Read), CommandKind.TriggerScene, null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-bool")]
    public void Validate_RejectsAMalformedBoolean(string? value) =>
        Assert.NotNull(CommandExpectation.Validate(Channel(DeviceChannelMetric.PowerState), CommandKind.SetPower, value));

    [Fact]
    public void Validate_RejectsAModeOutsideAvailableOptions() =>
        Assert.NotNull(CommandExpectation.Validate(
            Channel(DeviceChannelMetric.HvacMode, options: ["off", "cool"]), CommandKind.SetHvacMode, "heat"));

    [Fact]
    public void Validate_AllowsAnyMode_WhenOptionsAreUnknown() =>
        // A hand-added channel that never went through discovery has no options
        // list; that's a reason to pass the mode through, not to block it.
        Assert.Null(CommandExpectation.Validate(Channel(DeviceChannelMetric.HvacMode), CommandKind.SetHvacMode, "cool"));

    [Theory]
    [InlineData("true", "on")]
    [InlineData("false", "off")]
    public void For_SetPower_ExpectsHomeAssistantsOnOffStrings(string value, string expected) =>
        Assert.Equal(expected, CommandExpectation.For(CommandKind.SetPower, value)!.Value.Text);

    [Fact]
    public void For_SetTemperature_ExpectsANumericReading() =>
        Assert.Equal(72.5m, CommandExpectation.For(CommandKind.SetTemperature, "72.5")!.Value.Numeric);

    [Theory]
    [InlineData(CommandKind.TriggerScene)]
    [InlineData(CommandKind.PlayMedia)]
    public void For_ReturnsNothing_ForCommandsWithNoObservableChannelState(CommandKind kind) =>
        Assert.Null(CommandExpectation.For(kind, "anything"));

    [Theory]
    [InlineData(72.0, true)]
    [InlineData(71.5, true)]
    [InlineData(72.75, true)]
    [InlineData(71.0, false)]
    [InlineData(73.0, false)]
    public void Matches_AllowsRoundTripDriftButNotRealDivergence(double observed, bool expected) =>
        Assert.Equal(expected, CommandExpectation.Matches(
            new ExpectedState(72m, null), new ChannelLatestValue((decimal)observed, null, DateTimeOffset.UnixEpoch)));

    [Fact]
    public void Matches_ComparesModeStringsCaseInsensitively() =>
        Assert.True(CommandExpectation.Matches(
            new ExpectedState(null, "Cool"), new ChannelLatestValue(null, "cool", DateTimeOffset.UnixEpoch)));

    [Fact]
    public void Matches_IsFalse_WhenTheChannelHasNoSampleOfTheExpectedShape() =>
        // A numeric expectation can't be satisfied by a text sample, and vice
        // versa - Measurement and StateChange are separate tables for a reason.
        Assert.False(CommandExpectation.Matches(
            new ExpectedState(72m, null), new ChannelLatestValue(null, "72", DateTimeOffset.UnixEpoch)));
}
