using Hatch.Api.Ef;
using Hatch.Api.Services.Panels;

namespace Hatch.Api.Tests.Panels;

/// <summary>One test per rejection reason PanelBindingRules.Validate can return, plus the shapes it has to accept.</summary>
public class PanelBindingRulesTests
{
    private static EfDeviceChannel Channel(DeviceChannelMetric metric, ChannelDirection direction = ChannelDirection.ReadWrite) =>
        new() { Id = Guid.NewGuid(), DeviceId = Guid.NewGuid(), Metric = metric, HaEntityId = "climate.test", Direction = direction };

    /// <summary>Builds a control item bound to freshly-made channels, and the lookup Validate is handed alongside it.</summary>
    private static (EfPanelItem Item, Dictionary<Guid, EfDeviceChannel> Channels) Control(
        ControlKind kind, params (ControlRole Role, EfDeviceChannel Channel)[] bindings)
    {
        var item = new EfPanelItem
        {
            Id = Guid.NewGuid(),
            PanelId = Guid.NewGuid(),
            Kind = PanelItemKind.Control,
            ControlKind = kind,
            Label = "Air conditioner",
            Bindings = bindings
                .Select(b => new EfPanelControlBinding { Id = Guid.NewGuid(), Role = b.Role, ChannelId = b.Channel.Id })
                .ToList(),
        };
        return (item, bindings.ToDictionary(b => b.Channel.Id, b => b.Channel));
    }

    [Fact]
    public void RequiredMetric_PinsOneMetricPerRole()
    {
        Assert.Equal(DeviceChannelMetric.PowerState, PanelBindingRules.RequiredMetric(ControlRole.Power));
        Assert.Equal(DeviceChannelMetric.SetpointTemperature, PanelBindingRules.RequiredMetric(ControlRole.Setpoint));
        Assert.Equal(DeviceChannelMetric.HvacMode, PanelBindingRules.RequiredMetric(ControlRole.Mode));
        Assert.Equal(DeviceChannelMetric.Temperature, PanelBindingRules.RequiredMetric(ControlRole.Ambient));
    }

    [Fact]
    public void RolesFor_Switch_RequiresPowerAndNothingElse()
    {
        var roles = PanelBindingRules.RolesFor(ControlKind.Switch);

        Assert.Equal([new PanelRoleSpec(ControlRole.Power, Required: true)], roles);
    }

    [Fact]
    public void RolesFor_Thermostat_RequiresSetpointAndOffersThreeOptionalRoles()
    {
        var roles = PanelBindingRules.RolesFor(ControlKind.Thermostat);

        Assert.Equal([ControlRole.Setpoint], roles.Where(r => r.Required).Select(r => r.Role));
        Assert.Equal([ControlRole.Power, ControlRole.Mode, ControlRole.Ambient], roles.Where(r => !r.Required).Select(r => r.Role));
    }

    [Fact]
    public void Validate_ValidSwitch_IsAccepted()
    {
        var (item, channels) = Control(ControlKind.Switch, (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)));

        Assert.Null(PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_ValidThermostatWithPower_IsAccepted()
    {
        var (item, channels) = Control(
            ControlKind.Thermostat,
            (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)),
            (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)),
            (ControlRole.Ambient, Channel(DeviceChannelMetric.Temperature, ChannelDirection.Read)));

        Assert.Null(PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_ValidThermostatWithModeAndOnMode_IsAccepted()
    {
        var (item, channels) = Control(
            ControlKind.Thermostat,
            (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)),
            (ControlRole.Mode, Channel(DeviceChannelMetric.HvacMode)));
        item.OnMode = "cool";

        Assert.Null(PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_SetpointOnlyThermostat_IsAccepted()
    {
        var (item, channels) = Control(ControlKind.Thermostat, (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)));

        Assert.Null(PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_MissingRequiredRole_IsRejected()
    {
        var (item, channels) = Control(ControlKind.Thermostat, (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)));

        Assert.Equal("A Thermostat control requires a Setpoint channel.", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_WrongMetricForRole_IsRejected()
    {
        var (item, channels) = Control(ControlKind.Switch, (ControlRole.Power, Channel(DeviceChannelMetric.HvacMode)));

        Assert.Equal("Channel is a HvacMode channel; the Power role requires PowerState.", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_ReadOnlyChannelOnWritingRole_IsRejected()
    {
        var (item, channels) = Control(ControlKind.Switch, (ControlRole.Power, Channel(DeviceChannelMetric.PowerState, ChannelDirection.Read)));

        Assert.Equal("Channel is read-only; the Power role requires a ReadWrite channel.", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_RoleUnknownToTheKind_IsRejected()
    {
        var (item, channels) = Control(
            ControlKind.Switch,
            (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)),
            (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)));

        Assert.Equal("A Switch control has no Setpoint role.", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_ThermostatWithModeButNoOnMode_IsRejected()
    {
        var (item, channels) = Control(
            ControlKind.Thermostat,
            (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)),
            (ControlRole.Mode, Channel(DeviceChannelMetric.HvacMode)));

        Assert.Equal("A thermostat with a Mode channel needs an OnMode (e.g. \"cool\").", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_MinAtOrAboveMax_IsRejected()
    {
        var (item, channels) = Control(ControlKind.Thermostat, (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)));
        item.MinF = 80m;
        item.MaxF = 70m;

        Assert.Equal("MinF (80) must be below MaxF (70).", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_MinAboveDefaultMax_IsRejected_BecauseNullMeansTheDefault()
    {
        var (item, channels) = Control(ControlKind.Thermostat, (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)));
        item.MinF = 90m;

        Assert.Equal($"MinF (90) must be below MaxF ({PanelDefaults.MaxF}).", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_NonPositiveStep_IsRejected()
    {
        var (item, channels) = Control(ControlKind.Thermostat, (ControlRole.Setpoint, Channel(DeviceChannelMetric.SetpointTemperature)));
        item.StepF = 0m;

        Assert.Equal("StepF must be positive, got 0.", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_RoleBoundTwice_IsRejected()
    {
        var (item, channels) = Control(
            ControlKind.Switch,
            (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)),
            (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)));

        Assert.Equal("The Power role is bound more than once.", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_ChannelMissingFromLookup_IsRejectedRatherThanThrowing()
    {
        var (item, _) = Control(ControlKind.Switch, (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)));

        Assert.Equal("The channel bound to Power does not exist.", PanelBindingRules.Validate(item, new Dictionary<Guid, EfDeviceChannel>()));
    }

    [Fact]
    public void Validate_ControlWithNoKind_IsRejected()
    {
        var item = new EfPanelItem { Id = Guid.NewGuid(), PanelId = Guid.NewGuid(), Kind = PanelItemKind.Control };

        Assert.Equal("A control item must have a control kind.", PanelBindingRules.Validate(item, new Dictionary<Guid, EfDeviceChannel>()));
    }

    [Fact]
    public void Validate_ControlReferencingARoutine_IsRejected()
    {
        var (item, channels) = Control(ControlKind.Switch, (ControlRole.Power, Channel(DeviceChannelMetric.PowerState)));
        item.RoutineId = Guid.NewGuid();

        Assert.Equal("A control item cannot reference a routine.", PanelBindingRules.Validate(item, channels));
    }

    [Fact]
    public void Validate_RoutineItem_IsAcceptedWithARoutineAndNoBindings()
    {
        var item = new EfPanelItem { Id = Guid.NewGuid(), PanelId = Guid.NewGuid(), Kind = PanelItemKind.Routine, RoutineId = Guid.NewGuid() };

        Assert.Null(PanelBindingRules.Validate(item, new Dictionary<Guid, EfDeviceChannel>()));
    }

    [Fact]
    public void Validate_RoutineItemWithoutARoutine_IsRejected()
    {
        var item = new EfPanelItem { Id = Guid.NewGuid(), PanelId = Guid.NewGuid(), Kind = PanelItemKind.Routine };

        Assert.Equal("A routine item must reference a routine.", PanelBindingRules.Validate(item, new Dictionary<Guid, EfDeviceChannel>()));
    }

    [Fact]
    public void Validate_RoutineItemWithBindings_IsRejected()
    {
        var channel = Channel(DeviceChannelMetric.PowerState);
        var item = new EfPanelItem
        {
            Id = Guid.NewGuid(),
            PanelId = Guid.NewGuid(),
            Kind = PanelItemKind.Routine,
            RoutineId = Guid.NewGuid(),
            Bindings = [new EfPanelControlBinding { Id = Guid.NewGuid(), Role = ControlRole.Power, ChannelId = channel.Id }],
        };

        Assert.Equal("A routine item cannot bind channels.", PanelBindingRules.Validate(item, new Dictionary<Guid, EfDeviceChannel> { [channel.Id] = channel }));
    }
}
