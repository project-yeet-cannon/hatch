using System.Globalization;
using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Media;

namespace Aerie.Api.Services.Routines;

/// <summary>
/// Runs a Routine's actions against Home Assistant. Actions execute
/// sequentially in SortOrder - not in parallel - so a Routine's configured
/// order (e.g. "radiators off, then AC down, then fans on") is preserved and
/// HA isn't hit with a burst of concurrent service calls.
/// </summary>
public static class RoutineActionExecutor
{
    /// <param name="mediaLibraryBaseUrl">The MediaLibraryBaseUrl SiteSetting, used to resolve PlayMedia actions' stored library paths. Passed in rather than read here so this stays a pure dispatcher with no DB access of its own.</param>
    public static async Task ExecuteAsync(
        IReadOnlyList<EfRoutineAction> actions, IHomeAssistantCommandService command, string? mediaLibraryBaseUrl, CancellationToken ct)
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
                case RoutineActionKind.PlayMedia:
                    await command.PlayMediaAsync(entityId, ResolveMedia(action, mediaLibraryBaseUrl), MediaContentTypes.DefaultPlayMediaType);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(actions), action.Kind, "Unknown RoutineActionKind.");
            }
        }
    }

    /// <summary>Resolved here, at execution time, rather than stored resolved - see EfRoutineAction.Value. A path that no longer resolves (base URL cleared, say) fails the trigger loudly instead of silently sending the speaker something unfetchable.</summary>
    private static string ResolveMedia(EfRoutineAction action, string? mediaLibraryBaseUrl)
    {
        var (url, error) = MediaLibraryUrlResolver.Resolve(action.Value, mediaLibraryBaseUrl);
        return url ?? throw new InvalidOperationException($"RoutineAction {action.Id} can't play '{action.Value}': {error}");
    }

    private static bool ParseBool(string? value) =>
        bool.TryParse(value, out var result) && result;

    private static decimal ParseDecimal(string? value) =>
        decimal.Parse(RequireValue(value), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static string RequireValue(string? value) =>
        value ?? throw new InvalidOperationException("RoutineAction is missing its required Value.");
}
