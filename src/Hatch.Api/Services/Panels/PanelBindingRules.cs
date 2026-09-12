using Hatch.Api.Ef;

namespace Hatch.Api.Services.Panels;

/// <summary>One role a control kind may bind, and whether the kind is unusable without it.</summary>
public readonly record struct PanelRoleSpec(ControlRole Role, bool Required);

/// <summary>
/// Which channels a Panel Control is allowed to bind, and to what.
///
/// Pure - no DB, no clock, no HA client - and a sibling in spirit to
/// CommandExpectation, which asks the same question one level down: that class
/// pins the metric and direction a single CommandKind needs, this one pins the
/// metric and direction each ControlRole needs and which roles a ControlKind
/// can't do without. Both exist so the admin API, the kiosk write path and
/// (later) any other caller are held to one answer rather than three.
/// </summary>
public static class PanelBindingRules
{
    /// <summary>The metric a channel must carry to fill this role.</summary>
    public static DeviceChannelMetric RequiredMetric(ControlRole role) => role switch
    {
        ControlRole.Power => DeviceChannelMetric.PowerState,
        ControlRole.Setpoint => DeviceChannelMetric.SetpointTemperature,
        ControlRole.Mode => DeviceChannelMetric.HvacMode,
        ControlRole.Ambient => DeviceChannelMetric.Temperature,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown ControlRole."),
    };

    /// <summary>
    /// Whether the role is written as well as read. Ambient is the one
    /// read-only role - a room's temperature is reported, never commanded - so
    /// it accepts a Read channel where every other role demands ReadWrite.
    /// </summary>
    public static bool IsWritten(ControlRole role) => role != ControlRole.Ambient;

    /// <summary>The roles this kind understands, and which of them it requires. Any role not listed here is a binding error for that kind.</summary>
    public static IReadOnlyList<PanelRoleSpec> RolesFor(ControlKind kind) => kind switch
    {
        ControlKind.Switch =>
        [
            new PanelRoleSpec(ControlRole.Power, Required: true),
        ],
        ControlKind.Thermostat =>
        [
            new PanelRoleSpec(ControlRole.Setpoint, Required: true),
            new PanelRoleSpec(ControlRole.Power, Required: false),
            new PanelRoleSpec(ControlRole.Mode, Required: false),
            new PanelRoleSpec(ControlRole.Ambient, Required: false),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown ControlKind."),
    };

    /// <summary>
    /// Validates one panel item against the channels it binds, returning null
    /// when it's legal or the reason it isn't. Callers pass every referenced
    /// channel they could load, keyed by id; a binding whose channel is absent
    /// is reported as missing rather than throwing, because the admin API's
    /// lookup and the request's ids can legitimately disagree.
    ///
    /// A Thermostat with neither Power nor Mode is setpoint-only, which is
    /// legal - it just has no on/off for the kiosk to render.
    /// </summary>
    public static string? Validate(EfPanelItem item, IReadOnlyDictionary<Guid, EfDeviceChannel> channels)
    {
        if (item.Kind == PanelItemKind.Routine)
        {
            if (item.RoutineId is null) return "A routine item must reference a routine.";
            if (item.Bindings.Count > 0) return "A routine item cannot bind channels.";
            return null;
        }

        if (item.ControlKind is not { } kind) return "A control item must have a control kind.";
        if (item.RoutineId is not null) return "A control item cannot reference a routine.";

        var specs = RolesFor(kind);

        foreach (var binding in item.Bindings)
        {
            if (specs.All(s => s.Role != binding.Role))
                return $"A {kind} control has no {binding.Role} role.";

            if (item.Bindings.Count(b => b.Role == binding.Role) > 1)
                return $"The {binding.Role} role is bound more than once.";

            if (!channels.TryGetValue(binding.ChannelId, out var channel))
                return $"The channel bound to {binding.Role} does not exist.";

            var required = RequiredMetric(binding.Role);
            if (channel.Metric != required)
                return $"Channel is a {channel.Metric} channel; the {binding.Role} role requires {required}.";

            if (IsWritten(binding.Role) && channel.Direction != ChannelDirection.ReadWrite)
                return $"Channel is read-only; the {binding.Role} role requires a ReadWrite channel.";
        }

        foreach (var spec in specs.Where(s => s.Required))
        {
            if (item.Bindings.All(b => b.Role != spec.Role))
                return $"A {kind} control requires a {spec.Role} channel.";
        }

        if (kind == ControlKind.Thermostat)
        {
            // OnMode is what a bound Mode channel *means* for this device -
            // "cool" on the AC, "heat" on the radiator. Required whenever Mode
            // is bound, including alongside Power, so the stored answer never
            // depends on which other roles happen to be present.
            if (item.Bindings.Any(b => b.Role == ControlRole.Mode) && string.IsNullOrWhiteSpace(item.OnMode))
                return "A thermostat with a Mode channel needs an OnMode (e.g. \"cool\").";

            var min = item.MinF ?? PanelDefaults.MinF;
            var max = item.MaxF ?? PanelDefaults.MaxF;
            if (min >= max)
                return $"MinF ({min}) must be below MaxF ({max}).";

            if (item.StepF is { } step && step <= 0)
                return $"StepF must be positive, got {step}.";
        }

        return null;
    }
}
