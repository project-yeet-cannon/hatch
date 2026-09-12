using Hatch.Api.Ef;
using Hatch.Api.Services.Panels;
using Hatch.Api.Services.Routines;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.Panels;

/// <summary>Covers PanelService's tile filtering and its per-item state assembly against an EF Core InMemory database, in the same shape as RoutineServiceTests.</summary>
public class PanelServiceTests
{
    private static IDbContextFactory<AppDbContext> NewFactory() =>
        new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static EfDeviceChannel Channel(DeviceChannelMetric metric, ChannelDirection direction = ChannelDirection.ReadWrite) =>
        new() { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), Metric = metric, HaEntityId = "climate.ac", Direction = direction };

    private static EfPanelItem Control(ControlKind kind, string label, params (ControlRole Role, Guid ChannelId)[] bindings) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = PanelItemKind.Control,
            ControlKind = kind,
            Label = label,
            SortOrder = 0,
            Bindings = bindings
                .Select(b => new EfPanelControlBinding { Id = Guid.NewGuid(), Role = b.Role, ChannelId = b.ChannelId })
                .ToList(),
        };

    private static EfPanel Panel(params EfPanelItem[] items) =>
        new() { Id = Guid.NewGuid(), Name = "Climate", SortOrder = 0, Included = true, Items = items.ToList() };

    [Fact]
    public async Task GetPanelsAsync_ExcludesPanels_WhereIncludedIsFalse()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.AddRange(
                new EfPanel { Id = Guid.NewGuid(), Name = "Climate", SortOrder = 0, Included = true },
                new EfPanel { Id = Guid.NewGuid(), Name = "Hidden", SortOrder = 1, Included = false });
            await db.SaveChangesAsync();
        }

        var result = await new PanelService(factory).GetPanelsAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Climate", result[0].Name);
    }

    [Fact]
    public async Task GetPanelsAsync_OrdersBySortOrder_ThenName()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.AddRange(
                new EfPanel { Id = Guid.NewGuid(), Name = "Media", SortOrder = 1, Included = true },
                new EfPanel { Id = Guid.NewGuid(), Name = "Climate", SortOrder = 0, Included = true },
                new EfPanel { Id = Guid.NewGuid(), Name = "Environment", SortOrder = 1, Included = true });
            await db.SaveChangesAsync();
        }

        var result = await new PanelService(factory).GetPanelsAsync(CancellationToken.None);

        Assert.Equal(["Climate", "Environment", "Media"], result.Select(p => p.Name));
    }

    [Fact]
    public async Task GetPanelsAsync_CarriesTheTileAndNothingMore()
    {
        var factory = NewFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(new EfPanel { Id = Guid.NewGuid(), Name = "Climate", Icon = "snowflake", Color = "#4b7bec", SortOrder = 0, Included = true });
            await db.SaveChangesAsync();
        }

        var result = await new PanelService(factory).GetPanelsAsync(CancellationToken.None);

        Assert.Equal("snowflake", result[0].Icon);
        Assert.Equal("#4b7bec", result[0].Color);
    }

    [Fact]
    public async Task GetStateAsync_UnknownPanel_IsNull()
    {
        var factory = NewFactory();

        Assert.Null(await new PanelService(factory).GetStateAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetStateAsync_OrdersItemsBySortOrder()
    {
        var factory = NewFactory();
        var panel = Panel();
        var first = Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, Guid.NewGuid()));
        var second = Control(ControlKind.Switch, "Fan 2", (ControlRole.Power, Guid.NewGuid()));
        first.SortOrder = 1;
        second.SortOrder = 0;
        panel.Items = [first, second];
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.Equal(["Fan 2", "Fan 1"], state!.Items.Select(i => i.Label));
    }

    [Fact]
    public async Task GetStateAsync_SwitchIsOn_ComesFromThePowerChannel()
    {
        var factory = NewFactory();
        var power = Channel(DeviceChannelMetric.PowerState);
        var panel = Panel(Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, power.Id)));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            db.StateChanges.Add(new EfStateChange { Id = Guid.NewGuid(), ChannelId = power.Id, State = "on", Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.True(state!.Items[0].IsOn);
        Assert.Equal(ControlKind.Switch, state.Items[0].ControlKind);
    }

    [Fact]
    public async Task GetStateAsync_ThermostatIsOn_PrefersThePowerChannelOverMode()
    {
        var factory = NewFactory();
        var setpoint = Channel(DeviceChannelMetric.SetpointTemperature);
        var power = Channel(DeviceChannelMetric.PowerState);
        var mode = Channel(DeviceChannelMetric.HvacMode);
        var item = Control(ControlKind.Thermostat, "Air conditioner",
            (ControlRole.Setpoint, setpoint.Id), (ControlRole.Power, power.Id), (ControlRole.Mode, mode.Id));
        item.OnMode = "cool";
        var panel = Panel(item);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            // Power says off while the mode channel still reads "cool" - the
            // Power binding is the one that decides.
            db.StateChanges.AddRange(
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = power.Id, State = "off", Timestamp = DateTimeOffset.UtcNow },
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = mode.Id, State = "cool", Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.False(state!.Items[0].IsOn);
        Assert.Equal("cool", state.Items[0].Mode);
    }

    [Theory]
    [InlineData("cool", true)]
    [InlineData("heat", true)]
    [InlineData("off", false)]
    public async Task GetStateAsync_ThermostatWithoutPower_DerivesIsOnFromMode(string modeState, bool expected)
    {
        var factory = NewFactory();
        var setpoint = Channel(DeviceChannelMetric.SetpointTemperature);
        var mode = Channel(DeviceChannelMetric.HvacMode);
        var item = Control(ControlKind.Thermostat, "Air conditioner", (ControlRole.Setpoint, setpoint.Id), (ControlRole.Mode, mode.Id));
        item.OnMode = "cool";
        var panel = Panel(item);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            db.StateChanges.Add(new EfStateChange { Id = Guid.NewGuid(), ChannelId = mode.Id, State = modeState, Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.Equal(expected, state!.Items[0].IsOn);
    }

    [Fact]
    public async Task GetStateAsync_SetpointOnlyThermostat_HasNullIsOn()
    {
        var factory = NewFactory();
        var setpoint = Channel(DeviceChannelMetric.SetpointTemperature);
        var panel = Panel(Control(ControlKind.Thermostat, "Air conditioner", (ControlRole.Setpoint, setpoint.Id)));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            db.Measurements.Add(new EfMeasurement { Id = Guid.NewGuid(), ChannelId = setpoint.Id, Value = 72m, Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.Null(state!.Items[0].IsOn);
        Assert.Equal(72m, state.Items[0].SetpointF);
    }

    [Fact]
    public async Task GetStateAsync_ReadsSetpointAndAmbient_FromTheirOwnChannels()
    {
        var factory = NewFactory();
        var setpoint = Channel(DeviceChannelMetric.SetpointTemperature);
        var ambient = Channel(DeviceChannelMetric.Temperature, ChannelDirection.Read);
        var panel = Panel(Control(ControlKind.Thermostat, "Air conditioner",
            (ControlRole.Setpoint, setpoint.Id), (ControlRole.Ambient, ambient.Id)));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            db.Measurements.AddRange(
                new EfMeasurement { Id = Guid.NewGuid(), ChannelId = setpoint.Id, Value = 68m, Timestamp = DateTimeOffset.UtcNow },
                new EfMeasurement { Id = Guid.NewGuid(), ChannelId = ambient.Id, Value = 74.5m, Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.Equal(68m, state!.Items[0].SetpointF);
        Assert.Equal(74.5m, state.Items[0].AmbientF);
    }

    [Fact]
    public async Task GetStateAsync_ThermostatBounds_FallBackToPanelDefaults()
    {
        var factory = NewFactory();
        var setpoint = Channel(DeviceChannelMetric.SetpointTemperature);
        var withBounds = Control(ControlKind.Thermostat, "Radiator", (ControlRole.Setpoint, setpoint.Id));
        withBounds.MinF = 55m;
        withBounds.StepF = 0.5m;
        withBounds.SortOrder = 1;
        var panel = Panel(Control(ControlKind.Thermostat, "Air conditioner", (ControlRole.Setpoint, setpoint.Id)), withBounds);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.Equal(PanelDefaults.MinF, state!.Items[0].MinF);
        Assert.Equal(PanelDefaults.MaxF, state.Items[0].MaxF);
        Assert.Equal(PanelDefaults.StepF, state.Items[0].StepF);
        Assert.Equal(55m, state.Items[1].MinF);
        Assert.Equal(PanelDefaults.MaxF, state.Items[1].MaxF);
        Assert.Equal(0.5m, state.Items[1].StepF);
    }

    [Fact]
    public async Task GetStateAsync_Switch_HasNoBounds()
    {
        var factory = NewFactory();
        var panel = Panel(Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, Guid.NewGuid())));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.Null(state!.Items[0].MinF);
        Assert.Null(state.Items[0].MaxF);
        Assert.Null(state.Items[0].StepF);
    }

    [Fact]
    public async Task GetStateAsync_ControlWithNoSamplesYet_ReadsAllNull_RatherThanThrowing()
    {
        var factory = NewFactory();
        var item = Control(ControlKind.Thermostat, "Air conditioner",
            (ControlRole.Setpoint, Guid.NewGuid()), (ControlRole.Power, Guid.NewGuid()), (ControlRole.Ambient, Guid.NewGuid()));
        var panel = Panel(item);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.Add(panel);
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        var control = state!.Items[0];
        Assert.Null(control.IsOn);
        Assert.Null(control.SetpointF);
        Assert.Null(control.AmbientF);
        Assert.Null(control.Mode);
    }

    [Fact]
    public async Task GetStateAsync_RoutineItem_TakesItsLabelAndIconFromTheRoutine()
    {
        var factory = NewFactory();
        var routine = new EfRoutine { Id = Guid.NewGuid(), Name = "Night mode", Icon = "moon", Color = "#222", SortOrder = 0, Included = true };
        var panel = Panel(new EfPanelItem { Id = Guid.NewGuid(), Kind = PanelItemKind.Routine, RoutineId = routine.Id, SortOrder = 0 });
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(routine);
            db.Panels.Add(panel);
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);

        Assert.Equal("Night mode", state!.Items[0].Label);
        Assert.Equal("moon", state.Items[0].Icon);
        Assert.Equal("#222", state.Items[0].Color);
        Assert.False(state.Items[0].IsToggle);
        Assert.Null(state.Items[0].IsActive);
    }

    [Fact]
    public async Task GetStateAsync_ToggleRoutineItem_IsActiveMatchesRoutineService()
    {
        var factory = NewFactory();
        var lightA = Guid.NewGuid();
        var lightB = Guid.NewGuid();
        var routine = new EfRoutine
        {
            Id = Guid.NewGuid(),
            Name = "Outdoor floodlights",
            SortOrder = 0,
            Included = true,
            IsToggle = true,
            Actions =
            [
                new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = lightA, Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 0 },
                new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = lightB, Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 1 },
            ],
        };
        var panel = Panel(new EfPanelItem { Id = Guid.NewGuid(), Kind = PanelItemKind.Routine, RoutineId = routine.Id, SortOrder = 0 });
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(routine);
            db.Panels.Add(panel);
            db.StateChanges.AddRange(
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = lightA, State = "on", Timestamp = DateTimeOffset.UtcNow },
                new EfStateChange { Id = Guid.NewGuid(), ChannelId = lightB, State = "on", Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);
        var fromRoutineService = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.True(state!.Items[0].IsToggle);
        Assert.True(state.Items[0].IsActive);
        Assert.Equal(fromRoutineService[0].IsActive, state.Items[0].IsActive);
    }

    [Fact]
    public async Task GetStateAsync_ToggleRoutineItem_WithNoSamples_IsInactiveNotUnknown()
    {
        var factory = NewFactory();
        var routine = new EfRoutine
        {
            Id = Guid.NewGuid(),
            Name = "Outdoor floodlights",
            SortOrder = 0,
            Included = true,
            IsToggle = true,
            Actions = [new EfRoutineAction { Id = Guid.NewGuid(), ChannelId = Guid.NewGuid(), Kind = RoutineActionKind.SetPower, Value = "true", SortOrder = 0 }],
        };
        var panel = Panel(new EfPanelItem { Id = Guid.NewGuid(), Kind = PanelItemKind.Routine, RoutineId = routine.Id, SortOrder = 0 });
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Routines.Add(routine);
            db.Panels.Add(panel);
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(panel.Id, CancellationToken.None);
        var fromRoutineService = await new RoutineService(factory).GetRoutinesAsync(CancellationToken.None);

        Assert.False(state!.Items[0].IsActive);
        Assert.Equal(fromRoutineService[0].IsActive, state.Items[0].IsActive);
    }

    [Fact]
    public async Task GetStateAsync_IgnoresOtherPanelsItems()
    {
        var factory = NewFactory();
        var mine = Panel(Control(ControlKind.Switch, "Fan 1", (ControlRole.Power, Guid.NewGuid())));
        var theirs = Panel(Control(ControlKind.Switch, "Someone else's fan", (ControlRole.Power, Guid.NewGuid())));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Panels.AddRange(mine, theirs);
            await db.SaveChangesAsync();
        }

        var state = await new PanelService(factory).GetStateAsync(mine.Id, CancellationToken.None);

        Assert.Equal(["Fan 1"], state!.Items.Select(i => i.Label));
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new AppDbContext(options));
    }
}
