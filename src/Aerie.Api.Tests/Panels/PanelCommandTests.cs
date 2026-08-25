using Aerie.Api.Controllers;
using Aerie.Api.Ef;
using Aerie.Api.Models.Panels;
using Aerie.Api.Services.ClimateControl;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Panels;
using Aerie.Api.Tests.ClimateControl;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aerie.Api.Tests.Panels;

/// <summary>
/// Covers PanelsController's write path: which command a tap on a control turns
/// into, what the ledger says about it, and the admin CRUD's refusal to let an
/// invalid panel reach the database. The command service underneath is the real
/// one, with only Home Assistant faked - the point of routing panel writes
/// through it is that they get the same validation, ledgering and outcome
/// mapping as everything else, and a mocked-out chokepoint would test none of
/// that.
/// </summary>
public class PanelCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Power_DispatchesSetPower_ThroughThePowerBinding()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, item) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));

        Assert.IsType<NoContentResult>(await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(true), default));

        Assert.Equal(new HaCall("SetPower", "switch.fan_1", "True"), Assert.Single(h.Ha.Calls));
    }

    [Fact]
    public async Task Power_Off_DispatchesSetPowerFalse()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, item) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));

        await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(false), default);

        Assert.Equal("False", Assert.Single(h.Ha.Calls).Argument);
    }

    /// <summary>
    /// A thermostat with no power switch of its own is turned on by putting it
    /// into the mode that means "on" for that device - "cool" here, "heat" on
    /// the radiator this same control kind will drive later.
    /// </summary>
    [Fact]
    public async Task Power_FallsBackToOnMode_WhenNoPowerIsBound()
    {
        var h = NewHarness();
        var (panel, item) = await ThermostatAsync(h.Db, withPower: false);

        Assert.IsType<NoContentResult>(await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(true), default));

        Assert.Equal(new HaCall("SetHvacMode", "climate.ac", "cool"), Assert.Single(h.Ha.Calls));
    }

    [Fact]
    public async Task Power_Off_ViaOnModeFallback_DispatchesTheOffMode()
    {
        var h = NewHarness();
        var (panel, item) = await ThermostatAsync(h.Db, withPower: false);

        await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(false), default);

        Assert.Equal(new HaCall("SetHvacMode", "climate.ac", "off"), Assert.Single(h.Ha.Calls));
    }

    /// <summary>The same precedence the read path uses: a Power binding is the direct answer, and Mode is only consulted when there isn't one.</summary>
    [Fact]
    public async Task Power_PrefersThePowerBinding_WhenBothAreBound()
    {
        var h = NewHarness();
        var (panel, item) = await ThermostatAsync(h.Db, withPower: true);

        await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(true), default);

        Assert.Equal("SetPower", Assert.Single(h.Ha.Calls).Method);
    }

    [Fact]
    public async Task Power_IsBadRequest_OnASetpointOnlyThermostat()
    {
        var h = NewHarness();
        var setpoint = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        var (panel, item) = await AddPanelAsync(h.Db, Control(ControlKind.Thermostat, "Air conditioner", (ControlRole.Setpoint, setpoint.Id)));

        Assert.IsType<BadRequestObjectResult>(await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(true), default));
        Assert.Empty(h.Ha.Calls);
    }

    [Fact]
    public async Task Setpoint_ClampsBelowMin()
    {
        var h = NewHarness();
        var (panel, item) = await ThermostatAsync(h.Db, withPower: false, minF: 65m, maxF: 80m);

        await h.Controller.SetSetpoint(panel.Id, item.Id, new PanelSetpointRequest(50m), default);

        Assert.Equal("65", Assert.Single(h.Ha.Calls).Argument);
    }

    [Fact]
    public async Task Setpoint_ClampsAboveMax()
    {
        var h = NewHarness();
        var (panel, item) = await ThermostatAsync(h.Db, withPower: false, minF: 65m, maxF: 80m);

        await h.Controller.SetSetpoint(panel.Id, item.Id, new PanelSetpointRequest(99m), default);

        Assert.Equal("80", Assert.Single(h.Ha.Calls).Argument);
    }

    /// <summary>Snapped from the minimum, not from zero, so every reachable value is one the +/- buttons can also land on.</summary>
    [Fact]
    public async Task Setpoint_SnapsToTheStep()
    {
        var h = NewHarness();
        var (panel, item) = await ThermostatAsync(h.Db, withPower: false, minF: 60m, maxF: 85m, stepF: 5m);

        await h.Controller.SetSetpoint(panel.Id, item.Id, new PanelSetpointRequest(72m), default);

        Assert.Equal("70", Assert.Single(h.Ha.Calls).Argument);
    }

    /// <summary>A range the step doesn't divide evenly must still never command past the top of it.</summary>
    [Fact]
    public async Task Setpoint_StaysWithinBounds_WhenTheStepDoesNotDivideTheRange()
    {
        var h = NewHarness();
        // 60..73 by 5s: the top of the range is 13 above the minimum, so the
        // nearest step to 73 is 75 - above the bound the clamp exists for.
        var (panel, item) = await ThermostatAsync(h.Db, withPower: false, minF: 60m, maxF: 73m, stepF: 5m);

        await h.Controller.SetSetpoint(panel.Id, item.Id, new PanelSetpointRequest(73m), default);

        Assert.Equal("73", Assert.Single(h.Ha.Calls).Argument);
    }

    [Fact]
    public async Task Setpoint_IsBadRequest_OnASwitch()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, item) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));

        Assert.IsType<BadRequestObjectResult>(await h.Controller.SetSetpoint(panel.Id, item.Id, new PanelSetpointRequest(72m), default));
        Assert.Empty(h.Ha.Calls);
    }

    /// <summary>These endpoints address controls; a routine item names nothing they can act on, and the kiosk triggers it through the routines API instead.</summary>
    [Fact]
    public async Task Power_IsNotFound_OnARoutineItem()
    {
        var h = NewHarness();
        var routine = new EfRoutine { Name = "Movie night" };
        h.Db.Routines.Add(routine);
        await h.Db.SaveChangesAsync();
        var (panel, item) = await AddPanelAsync(h.Db, new EfPanelItem { Kind = PanelItemKind.Routine, RoutineId = routine.Id });

        Assert.IsType<NotFoundResult>(await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(true), default));
    }

    [Fact]
    public async Task Power_IsNotFound_WhenTheItemBelongsToAnotherPanel()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (_, item) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));
        var (other, _) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 2", (ControlRole.Power, channel.Id)), "Environment");

        Assert.IsType<NotFoundResult>(await h.Controller.SetPower(other.Id, item.Id, new PanelPowerRequest(true), default));
    }

    /// <summary>The whole reason panel writes go through the command service: the ledger says which surface a hand touched, not just that a hand was involved.</summary>
    [Fact]
    public async Task Dispatch_LedgersTheCommand_AsHumanNamingThePanelAndTheControl()
    {
        var h = NewHarness();
        var (panel, item) = await ThermostatAsync(h.Db, withPower: false);

        await h.Controller.SetSetpoint(panel.Id, item.Id, new PanelSetpointRequest(72m), default);

        var command = Assert.Single(await h.Db.Commands.ToListAsync());
        Assert.Equal(CommandSource.Human, command.Source);
        Assert.Equal(CommandOutcome.Succeeded, command.Outcome);
        Assert.Equal("Panel 'Climate' — Air conditioner", command.Reason);
    }

    /// <summary>
    /// A channel can be turned read-only after a panel bound it, so the write
    /// path meets rejections the admin-time rules already passed. Rejected is
    /// the caller asking for something illegal - 400.
    /// </summary>
    [Fact]
    public async Task Dispatch_MapsRejected_To400()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, item) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));

        channel.Direction = ChannelDirection.Read;
        await h.Db.SaveChangesAsync();

        Assert.IsType<BadRequestObjectResult>(await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(true), default));
    }

    /// <summary>And a Home Assistant that won't take the call is an upstream failure - 502, exactly as the routine endpoints report it.</summary>
    [Fact]
    public async Task Dispatch_MapsFailed_To502()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, item) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));
        h.Ha.ThrowOnCall = new HttpRequestException("no route to host");

        var result = Assert.IsType<ObjectResult>(await h.Controller.SetPower(panel.Id, item.Id, new PanelPowerRequest(true), default));

        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
    }

    [Fact]
    public async Task Create_RefusesAnInvalidPanel_AndSavesNothing()
    {
        var h = NewHarness();
        var wrongMetric = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.Temperature, "sensor.living_room");
        var request = Write(new PanelItemWriteRequest(
            0, PanelItemKind.Control, null, ControlKind.Switch, "Fan 1", null, null, null, null, null, null,
            [new PanelControlBindingWriteRequest(ControlRole.Power, wrongMetric.Id)]));

        var result = await h.Controller.Create(request, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await h.Db.Panels.ToListAsync());
        Assert.Empty(await h.Db.PanelItems.ToListAsync());
    }

    /// <summary>A binding pointing at nothing is a bad request, not a foreign-key crash - PanelBindingRules reports the missing channel for exactly this caller.</summary>
    [Fact]
    public async Task Create_RefusesABindingToAChannelThatDoesNotExist()
    {
        var h = NewHarness();
        var request = Write(new PanelItemWriteRequest(
            0, PanelItemKind.Control, null, ControlKind.Switch, "Fan 1", null, null, null, null, null, null,
            [new PanelControlBindingWriteRequest(ControlRole.Power, Guid.NewGuid())]));

        Assert.IsType<BadRequestObjectResult>((await h.Controller.Create(request, default)).Result);
        Assert.Empty(await h.Db.Panels.ToListAsync());
    }

    [Fact]
    public async Task Create_RefusesARoutineItemPointingAtNoRoutine()
    {
        var h = NewHarness();
        var request = Write(new PanelItemWriteRequest(
            0, PanelItemKind.Routine, Guid.NewGuid(), null, null, null, null, null, null, null, null, []));

        Assert.IsType<BadRequestObjectResult>((await h.Controller.Create(request, default)).Result);
        Assert.Empty(await h.Db.Panels.ToListAsync());
    }

    [Fact]
    public async Task Create_SavesTheItemsAndTheirBindings()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var request = Write(new PanelItemWriteRequest(
            0, PanelItemKind.Control, null, ControlKind.Switch, "Fan 1", "fan", "#4b7bec", null, null, null, null,
            [new PanelControlBindingWriteRequest(ControlRole.Power, channel.Id)]));

        var created = (CreatedAtActionResult)(await h.Controller.Create(request, default)).Result!;
        var panel = (PanelDto)created.Value!;

        var item = Assert.Single(panel.Items);
        Assert.Equal("Fan 1", item.Label);
        Assert.Equal(channel.Id, Assert.Single(item.Bindings).ChannelId);
    }

    /// <summary>The item list *is* the panel - an update replaces it wholesale, taking the old bindings with it rather than leaving them behind their deleted items.</summary>
    [Fact]
    public async Task Update_ReplacesTheItemsWholesale()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, _) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));

        var request = Write(new PanelItemWriteRequest(
            0, PanelItemKind.Control, null, ControlKind.Switch, "Fan 2", null, null, null, null, null, null,
            [new PanelControlBindingWriteRequest(ControlRole.Power, channel.Id)]));
        var updated = (await h.Controller.Update(panel.Id, request, default)).Value!;

        Assert.Equal("Fan 2", Assert.Single(updated.Items).Label);
        Assert.Single(await h.Db.PanelItems.ToListAsync());
        Assert.Single(await h.Db.PanelControlBindings.ToListAsync());
    }

    [Fact]
    public async Task Update_RefusesAnInvalidPanel_AndLeavesTheStoredOneAlone()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, _) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));

        // A Switch with no Power binding at all.
        var request = Write(new PanelItemWriteRequest(
            0, PanelItemKind.Control, null, ControlKind.Switch, "Fan 2", null, null, null, null, null, null, []));

        Assert.IsType<BadRequestObjectResult>((await h.Controller.Update(panel.Id, request, default)).Result);
        Assert.Equal("Fan 1", Assert.Single(await h.Db.PanelItems.ToListAsync()).Label);
    }

    [Fact]
    public async Task GetState_IsNotFound_ForAPanelThatIsNotThere()
    {
        var h = NewHarness();

        Assert.IsType<NotFoundResult>((await h.Controller.GetState(Guid.NewGuid(), default)).Result);
    }

    [Fact]
    public async Task GetState_ReturnsThePanelsItems()
    {
        var h = NewHarness();
        var channel = await ClimateControlFakes.AddChannelAsync(h.Db, DeviceChannelMetric.PowerState, "switch.fan_1");
        var (panel, item) = await AddPanelAsync(h.Db, Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, channel.Id)));

        var state = (await h.Controller.GetState(panel.Id, default)).Value!;

        Assert.Equal("Climate", state.Name);
        Assert.Equal(item.Id, Assert.Single(state.Items).Id);
    }

    private static PanelWriteRequest Write(params PanelItemWriteRequest[] items) =>
        new("Climate", null, "snowflake", "#4b7bec", 0, true, items);

    private static EfPanelItem Control(ControlKind kind, string label, params (ControlRole Role, Guid ChannelId)[] bindings) =>
        new()
        {
            Kind = PanelItemKind.Control,
            ControlKind = kind,
            Label = label,
            Bindings = bindings.Select(b => new EfPanelControlBinding { Role = b.Role, ChannelId = b.ChannelId }).ToList(),
        };

    /// <summary>An air conditioner with a setpoint and a mode channel, optionally with a power switch of its own.</summary>
    private static async Task<(EfPanel Panel, EfPanelItem Item)> ThermostatAsync(
        AerieContext db, bool withPower, decimal? minF = null, decimal? maxF = null, decimal? stepF = null)
    {
        var setpoint = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.SetpointTemperature, "climate.ac");
        var mode = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.HvacMode, "climate.ac", options: ["off", "cool", "heat"]);

        var bindings = new List<(ControlRole, Guid)> { (ControlRole.Setpoint, setpoint.Id), (ControlRole.Mode, mode.Id) };
        if (withPower)
        {
            var power = await ClimateControlFakes.AddChannelAsync(db, DeviceChannelMetric.PowerState, "climate.ac");
            bindings.Add((ControlRole.Power, power.Id));
        }

        var item = Control(ControlKind.Thermostat, "Air conditioner", bindings.ToArray());
        item.OnMode = "cool";
        item.MinF = minF;
        item.MaxF = maxF;
        item.StepF = stepF;
        return await AddPanelAsync(db, item);
    }

    private static async Task<(EfPanel Panel, EfPanelItem Item)> AddPanelAsync(AerieContext db, EfPanelItem item, string name = "Climate")
    {
        var panel = new EfPanel { Name = name, SortOrder = 0, Included = true, Items = [item] };
        db.Panels.Add(panel);
        await db.SaveChangesAsync();
        return (panel, item);
    }

    private static Harness NewHarness()
    {
        var options = new DbContextOptionsBuilder<AerieContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new AerieContext(options);
        var ha = new FakeHomeAssistantCommandService();
        var commands = new ClimateCommandService(
            db, ha, new FakeSiteSettingsService(), new FakeTimeProvider(Now), NullLogger<ClimateCommandService>.Instance);
        var controller = new PanelsController(db, new PanelService(new TestDbContextFactory(options)), commands);
        return new Harness(controller, db, ha);
    }

    private sealed record Harness(PanelsController Controller, AerieContext Db, FakeHomeAssistantCommandService Ha);

    private sealed class TestDbContextFactory(DbContextOptions<AerieContext> options) : IDbContextFactory<AerieContext>
    {
        public AerieContext CreateDbContext() => new(options);
    }
}
