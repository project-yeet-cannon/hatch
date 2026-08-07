using Aerie.Api.Ef;
using Aerie.Api.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.ClimateControl;

/// <summary>
/// Covers ReconcileCommands' two jobs - confirming that a command took effect,
/// and noticing when something outside Aerie later undid it. The conservative
/// rule under test throughout: nothing counts as an override unless Aerie first
/// observed its own value in place.
/// </summary>
public class ReconcileCommandsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 14, 0, 0, TimeSpan.Zero);

    private static ReconcileCommands NewJob(AerieContext db, DateTimeOffset now, int backoffMinutes = 120) =>
        new(new FakeTimeProvider(now), db, new FakeSiteSettingsService(overrideBackoffMinutes: backoffMinutes),
            NullLogger<ReconcileCommands>.Instance);

    private static EfCommand Dispatched(
        Guid channelId, CommandKind kind, string? value, DateTimeOffset at, DateTimeOffset? confirmedAt = null) => new()
        {
            ChannelId = channelId,
            Kind = kind,
            Value = value,
            Source = CommandSource.Controller,
            RequestedAt = at,
            DispatchedAt = at,
            Outcome = CommandOutcome.Succeeded,
            ConfirmedAt = confirmedAt,
        };

    [Fact]
    public async Task Reconcile_ConfirmsSetpoint_WhenObservedValueMatches()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetTemperature, "72", Now.AddMinutes(-3)));
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), Value = 72m });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        var command = await db.Commands.AsNoTracking().SingleAsync();
        Assert.Equal(Now.AddMinutes(-1), command.ConfirmedAt);
        Assert.Equal("72", command.ConfirmedValue);
    }

    [Fact]
    public async Task Reconcile_ConfirmsSetpoint_WithinRoundTripTolerance()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetTemperature, "72", Now.AddMinutes(-3)));
        // A setpoint makes an F -> C -> F round trip through HA and the
        // thermostat, landing a few tenths off what was sent.
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), Value = 71.6m });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        Assert.NotNull((await db.Commands.AsNoTracking().SingleAsync()).ConfirmedAt);
    }

    [Fact]
    public async Task Reconcile_DoesNotConfirm_WhenThermostatClampedTheRequest()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetTemperature, "60", Now.AddMinutes(-3)));
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), Value = 62m });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        // Left unconfirmed rather than quietly agreeing: the gap between what
        // was asked for and what the device would accept is the signal.
        Assert.Null((await db.Commands.AsNoTracking().SingleAsync()).ConfirmedAt);
    }

    [Fact]
    public async Task Reconcile_ConfirmsPowerState_FromStateChange()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.PowerState, "switch.fan");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetPower, "true", Now.AddMinutes(-3)));
        db.StateChanges.Add(new EfStateChange { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), State = "on" });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        Assert.Equal("on", (await db.Commands.AsNoTracking().SingleAsync()).ConfirmedValue);
    }

    [Fact]
    public async Task Reconcile_DetectsOverride_WhenConfirmedValueLaterDiverges()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetTemperature, "72", Now.AddHours(-2), confirmedAt: Now.AddHours(-2).AddMinutes(1)));
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), Value = 68m });
        await db.SaveChangesAsync();

        await NewJob(db, Now, backoffMinutes: 120).ReconcileAsync(CancellationToken.None);

        var detected = await db.ControlOverrides.AsNoTracking().SingleAsync();
        Assert.Equal(channel.Id, detected.ChannelId);
        Assert.Equal(channel.DeviceId, detected.DeviceId);
        Assert.Equal("72", detected.ExpectedState);
        Assert.Equal("68", detected.ObservedState);
        Assert.Equal(Now.AddHours(2), detected.SuppressedUntil);
        Assert.Equal(Now, (await db.Commands.AsNoTracking().SingleAsync()).OverriddenAt);
    }

    [Fact]
    public async Task Reconcile_NeverTreatsAnUnconfirmedCommandAsAnOverrideBaseline()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetTemperature, "72", Now.AddHours(-2)));
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), Value = 68m });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        // Without having seen 72 in place first, "the channel reads 68" is just
        // as likely to mean the command never took as it is to mean somebody
        // countermanded it - and inventing an override out of the former would
        // have the controller disabling itself for reasons nobody could explain.
        Assert.Empty(db.ControlOverrides);
    }

    [Fact]
    public async Task Reconcile_RecordsOneOverridePerCommand_NotOnePerRun()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetTemperature, "72", Now.AddHours(-2), confirmedAt: Now.AddHours(-2).AddMinutes(1)));
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), Value = 68m });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);
        await NewJob(db, Now.AddMinutes(2)).ReconcileAsync(CancellationToken.None);
        await NewJob(db, Now.AddMinutes(4)).ReconcileAsync(CancellationToken.None);

        Assert.Single(db.ControlOverrides);
    }

    [Fact]
    public async Task Reconcile_IgnoresObservationsOlderThanTheConfirmation()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.Commands.Add(Dispatched(channel.Id, CommandKind.SetTemperature, "72", Now.AddHours(-2), confirmedAt: Now.AddMinutes(-1)));
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-30), Value = 68m });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        Assert.Empty(db.ControlOverrides);
    }

    [Fact]
    public async Task Reconcile_OnlyLooksAtTheMostRecentCommandForAChannel()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        var superseded = Dispatched(channel.Id, CommandKind.SetTemperature, "68", Now.AddHours(-3), confirmedAt: Now.AddHours(-3));
        var current = Dispatched(channel.Id, CommandKind.SetTemperature, "72", Now.AddMinutes(-5));
        db.Commands.AddRange(superseded, current);
        db.Measurements.Add(new EfMeasurement { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), Value = 72m });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        // The superseded command must not be read as overridden just because the
        // channel has since moved on to what the newer command asked for.
        Assert.Empty(db.ControlOverrides);
        Assert.NotNull((await db.Commands.AsNoTracking().SingleAsync(c => c.Id == current.Id)).ConfirmedAt);
        Assert.Null((await db.Commands.AsNoTracking().SingleAsync(c => c.Id == superseded.Id)).OverriddenAt);
    }

    [Fact]
    public async Task Reconcile_SkipsCommandsWithNoObservableEffect()
    {
        await using var db = ClimateControlFakes.NewContext();
        var scene = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.Scene, "scene.night");
        db.Commands.Add(Dispatched(scene.Id, CommandKind.TriggerScene, null, Now.AddMinutes(-5)));
        db.StateChanges.Add(new EfStateChange { ChannelId = scene.Id, Timestamp = Now.AddMinutes(-1), State = "2026-08-07T13:55:00" });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        // A scene is a stateless trigger - there's nothing to compare against,
        // so it stays unconfirmed rather than being confirmed against something
        // that was never verified.
        Assert.Null((await db.Commands.AsNoTracking().SingleAsync()).ConfirmedAt);
        Assert.Empty(db.ControlOverrides);
    }

    [Fact]
    public async Task Reconcile_IgnoresCommandsThatNeverReachedHomeAssistant()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.PowerState, "switch.fan");
        db.Commands.Add(new EfCommand
        {
            ChannelId = channel.Id,
            Kind = CommandKind.SetPower,
            Value = "true",
            Source = CommandSource.Controller,
            RequestedAt = Now.AddMinutes(-5),
            Outcome = CommandOutcome.Suppressed,
        });
        db.StateChanges.Add(new EfStateChange { ChannelId = channel.Id, Timestamp = Now.AddMinutes(-1), State = "on" });
        await db.SaveChangesAsync();

        await NewJob(db, Now).ReconcileAsync(CancellationToken.None);

        Assert.Null((await db.Commands.AsNoTracking().SingleAsync()).ConfirmedAt);
    }
}
