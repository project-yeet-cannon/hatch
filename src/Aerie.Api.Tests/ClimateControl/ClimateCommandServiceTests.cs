using Aerie.Api.Ef;
using Aerie.Api.Services.ClimateControl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.ClimateControl;

/// <summary>
/// Covers ClimateCommandService's contract as the single chokepoint for writes
/// to Home Assistant: every attempt lands in the ledger, illegal ones never
/// reach HA, and autonomous ones defer to an active manual override.
/// </summary>
public class ClimateCommandServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 14, 0, 0, TimeSpan.Zero);

    private static (ClimateCommandService Service, FakeHomeAssistantCommandService Ha) NewService(
        AerieContext db, string? mediaBaseUrl = null) =>
        NewService(db, new FakeHomeAssistantCommandService(), mediaBaseUrl);

    private static (ClimateCommandService Service, FakeHomeAssistantCommandService Ha) NewService(
        AerieContext db, FakeHomeAssistantCommandService ha, string? mediaBaseUrl = null) =>
        (new ClimateCommandService(db, ha, new FakeSiteSettingsService(mediaBaseUrl), new FakeTimeProvider(Now),
            NullLogger<ClimateCommandService>.Instance), ha);

    [Fact]
    public async Task DispatchAsync_Succeeds_RecordsLedgerRowAndCallsHa()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.PowerState, "switch.fan");
        var (service, ha) = NewService(db);

        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetPower, "true", CommandSource.Controller, "too warm in the den"),
            CancellationToken.None);

        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        Assert.Equal(new HaCall("SetPower", "switch.fan", "True"), Assert.Single(ha.Calls));

        var command = await db.Commands.SingleAsync();
        Assert.Equal(CommandOutcome.Succeeded, command.Outcome);
        Assert.Equal(CommandSource.Controller, command.Source);
        Assert.Equal("too warm in the den", command.Reason);
        Assert.Equal(Now, command.RequestedAt);
        Assert.Equal(Now, command.DispatchedAt);
        Assert.Null(command.ConfirmedAt);
    }

    [Fact]
    public async Task DispatchAsync_WrongMetricForKind_IsRejectedWithoutCallingHa()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.Temperature, "sensor.den", ChannelDirection.Read);
        var (service, ha) = NewService(db);

        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetPower, "true", CommandSource.Human, "Admin UI"), CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(ha.Calls);

        // Rejected attempts are still ledgered - an action the guards refused is
        // exactly what someone debugging the controller needs to be able to see.
        var command = await db.Commands.SingleAsync();
        Assert.Equal(CommandOutcome.Rejected, command.Outcome);
        Assert.Null(command.DispatchedAt);
        Assert.Contains("SetPower requires", command.Error);
    }

    [Fact]
    public async Task DispatchAsync_ReadOnlyChannel_IsRejected()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(
            db, DeviceChannelMetric.SetpointTemperature, "climate.ac", ChannelDirection.Read);
        var (service, ha) = NewService(db);

        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetTemperature, "72", CommandSource.Human, null), CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Contains("read-only", result.Error);
        Assert.Empty(ha.Calls);
    }

    [Fact]
    public async Task DispatchAsync_ModeOutsideAvailableOptions_IsRejected()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(
            db, DeviceChannelMetric.HvacMode, "climate.ac", options: ["off", "cool", "fan_only"]);
        var (service, ha) = NewService(db);

        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetHvacMode, "heat", CommandSource.Controller, null), CancellationToken.None);

        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Empty(ha.Calls);
    }

    [Fact]
    public async Task DispatchAsync_HaThrows_RecordsFailureWithError()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.PowerState, "switch.fan");
        var ha = new FakeHomeAssistantCommandService { ThrowOnCall = new HttpRequestException("HA unreachable") };
        var (service, _) = NewService(db, ha);

        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetPower, "false", CommandSource.Routine, "Routine 'Night'"), CancellationToken.None);

        Assert.Equal(CommandOutcome.Failed, result.Outcome);

        var command = await db.Commands.SingleAsync();
        Assert.Equal(CommandOutcome.Failed, command.Outcome);
        Assert.Equal("HA unreachable", command.Error);
        // Dispatch was attempted, so the row records when - unlike a rejection,
        // which never got that far.
        Assert.Equal(Now, command.DispatchedAt);
    }

    [Fact]
    public async Task DispatchAsync_ControllerCommand_IsSuppressedByActiveOverride()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.ControlOverrides.Add(new EfControlOverride
        {
            DeviceId = channel.DeviceId,
            ChannelId = channel.Id,
            CommandId = Guid.NewGuid(),
            DetectedAt = Now.AddMinutes(-10),
            SuppressedUntil = Now.AddHours(1),
        });
        await db.SaveChangesAsync();
        var (service, ha) = NewService(db);

        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetTemperature, "70", CommandSource.Controller, null), CancellationToken.None);

        Assert.Equal(CommandOutcome.Suppressed, result.Outcome);
        Assert.Empty(ha.Calls);
    }

    [Fact]
    public async Task DispatchAsync_HumanCommand_IgnoresActiveOverride()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.ControlOverrides.Add(new EfControlOverride
        {
            DeviceId = channel.DeviceId,
            ChannelId = channel.Id,
            CommandId = Guid.NewGuid(),
            DetectedAt = Now.AddMinutes(-10),
            SuppressedUntil = Now.AddHours(1),
        });
        await db.SaveChangesAsync();
        var (service, ha) = NewService(db);

        // A person telling Aerie what to do is the opposite of the situation an
        // override describes - refusing them would make the app look broken.
        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetTemperature, "70", CommandSource.Human, "Admin UI"), CancellationToken.None);

        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        Assert.Single(ha.Calls);
    }

    [Fact]
    public async Task DispatchAsync_ExpiredOverride_NoLongerSuppresses()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        db.ControlOverrides.Add(new EfControlOverride
        {
            DeviceId = channel.DeviceId,
            ChannelId = channel.Id,
            CommandId = Guid.NewGuid(),
            DetectedAt = Now.AddHours(-5),
            SuppressedUntil = Now.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
        var (service, ha) = NewService(db);

        var result = await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.SetTemperature, "70", CommandSource.Controller, null), CancellationToken.None);

        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        Assert.Single(ha.Calls);
    }

    [Fact]
    public async Task DispatchAsync_PlayMedia_LedgersThePathButSendsTheResolvedUrl()
    {
        await using var db = ClimateControlFakes.NewContext();
        var channel = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.MediaPlayback, "media_player.deck");
        var (service, ha) = NewService(db, mediaBaseUrl: "https://home.example.com/media");

        await service.DispatchAsync(
            new CommandRequest(channel.Id, CommandKind.PlayMedia, "Miles Davis/So What.flac", CommandSource.Routine, null),
            CancellationToken.None);

        Assert.Equal("https://home.example.com/media/Miles%20Davis/So%20What.flac", Assert.Single(ha.Calls).Argument);
        // The ledger keeps the library-relative path: a base URL that changes
        // shouldn't retroactively make old rows point somewhere that no longer exists.
        Assert.Equal("Miles Davis/So What.flac", (await db.Commands.SingleAsync()).Value);
    }

    [Fact]
    public async Task DispatchManyAsync_StopsAtFirstFailure()
    {
        await using var db = ClimateControlFakes.NewContext();
        var good = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.PowerState, "switch.one");
        var bad = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.Temperature, "sensor.two", ChannelDirection.Read);
        var never = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.PowerState, "switch.three");
        var (service, ha) = NewService(db);

        var results = await service.DispatchManyAsync(
        [
            new CommandRequest(good.Id, CommandKind.SetPower, "true", CommandSource.Routine, null),
            new CommandRequest(bad.Id, CommandKind.SetPower, "true", CommandSource.Routine, null),
            new CommandRequest(never.Id, CommandKind.SetPower, "true", CommandSource.Routine, null),
        ], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(CommandOutcome.Rejected, results[1].Outcome);
        // A routine's order is meaningful, so the third action must not run
        // after the second one failed.
        Assert.Equal("switch.one", Assert.Single(ha.Calls).EntityId);
    }

    [Fact]
    public async Task DispatchAsync_UnknownChannel_Throws()
    {
        await using var db = ClimateControlFakes.NewContext();
        var (service, _) = NewService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DispatchAsync(
            new CommandRequest(Guid.NewGuid(), CommandKind.SetPower, "true", CommandSource.Human, null), CancellationToken.None));
    }
}
