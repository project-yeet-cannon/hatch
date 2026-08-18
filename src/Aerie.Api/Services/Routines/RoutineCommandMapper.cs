using Aerie.Api.Ef;
using Aerie.Api.Services.ClimateControl;

namespace Aerie.Api.Services.Routines;

/// <summary>
/// Turns a Routine's actions into ledgered CommandRequests, in SortOrder.
///
/// Replaces the old RoutineActionExecutor, which called Home Assistant
/// directly. Everything that made that class more than a mapper - value
/// parsing, media URL resolution, ordering the HA calls - now belongs to
/// ClimateCommandService, which applies the same rules to routines, admin
/// writes, and (later) the control loop alike. What's left is a pure
/// translation with no DB, clock, or HA client, which is also why it no longer
/// needs the action's Channel navigation property loaded: the command service
/// resolves the channel itself.
/// </summary>
public static class RoutineCommandMapper
{
    /// <param name="reason">Recorded on every resulting EfCommand, so the ledger says which routine an actuation came from rather than just "Routine".</param>
    public static IReadOnlyList<CommandRequest> ToCommandRequests(IReadOnlyList<EfRoutineAction> actions, string reason) =>
        actions
            .OrderBy(a => a.SortOrder)
            .Select(a => new CommandRequest(a.ChannelId, ToCommandKind(a.Kind), a.Value, CommandSource.Routine, reason))
            .ToList();

    /// <summary>
    /// The "off" half of a toggle routine (EfRoutine.IsToggle): forces SetPower
    /// "false" onto every SetPower action's channel, ignoring the action's
    /// stored (on) Value. Other action kinds aren't included - a toggle routine
    /// only has a well-defined inverse for power, so the admin UI restricts
    /// toggle routines to SetPower actions in the first place.
    /// </summary>
    public static IReadOnlyList<CommandRequest> ToOffCommandRequests(IReadOnlyList<EfRoutineAction> actions, string reason) =>
        actions
            .Where(a => a.Kind == RoutineActionKind.SetPower)
            .OrderBy(a => a.SortOrder)
            .Select(a => new CommandRequest(a.ChannelId, CommandKind.SetPower, "false", CommandSource.Routine, reason))
            .ToList();

    public static CommandKind ToCommandKind(RoutineActionKind kind) => kind switch
    {
        RoutineActionKind.SetPower => CommandKind.SetPower,
        RoutineActionKind.SetTemperature => CommandKind.SetTemperature,
        RoutineActionKind.SetHvacMode => CommandKind.SetHvacMode,
        RoutineActionKind.SetFanMode => CommandKind.SetFanMode,
        RoutineActionKind.TriggerScene => CommandKind.TriggerScene,
        RoutineActionKind.PlayMedia => CommandKind.PlayMedia,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown RoutineActionKind."),
    };
}
