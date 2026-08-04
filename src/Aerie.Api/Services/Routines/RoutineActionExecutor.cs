using System.Globalization;
using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;

namespace Aerie.Api.Services.Routines;

/// <summary>
/// Runs a Routine's actions against Home Assistant. Actions execute
/// sequentially in SortOrder - not in parallel - so a Routine's configured
/// order (e.g. "radiators off, then AC down, then fans on") is preserved and
/// HA isn't hit with a burst of concurrent service calls.
/// </summary>
public static class RoutineActionExecutor
{
    public static async Task ExecuteAsync(
        IReadOnlyList<EfRoutineAction> actions, IHomeAssistantCommandService command, CancellationToken ct)
    {
        foreach (var action in actions.OrderBy(a => a.SortOrder))
        {
            ct.ThrowIfCancellationRequested();
            if (action.Channel is null)
                throw new InvalidOperationException($"RoutineAction {action.Id} has no loaded Channel.");

            var entityId = action.Channel.HaEntityId;
            switch (action.Kind)
            {
                case RoutineActionKind.SetPower:
                    await command.SetPowerAsync(entityId, ParseBool(action.Value));
                    break;
                case RoutineActionKind.SetTemperature:
                    await command.SetTemperatureAsync(entityId, ParseDecimal(action.Value));
                    break;
                case RoutineActionKind.SetHvacMode:
                    await command.SetHvacModeAsync(entityId, RequireValue(action.Value));
                    break;
                case RoutineActionKind.SetFanMode:
                    await command.SetFanModeAsync(entityId, RequireValue(action.Value));
                    break;
                case RoutineActionKind.TriggerScene:
                    await command.TriggerSceneAsync(entityId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(actions), action.Kind, "Unknown RoutineActionKind.");
            }
        }
    }

    private static bool ParseBool(string? value) =>
        bool.TryParse(value, out var result) && result;

    private static decimal ParseDecimal(string? value) =>
        decimal.Parse(RequireValue(value), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string RequireValue(string? value) =>
        value ?? throw new InvalidOperationException("RoutineAction is missing its required Value.");
}
