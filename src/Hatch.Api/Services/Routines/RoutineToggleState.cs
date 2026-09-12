using Hatch.Api.Ef;
using Hatch.Api.Services.DeviceMapping;

namespace Hatch.Api.Services.Routines;

/// <summary>
/// Whether a toggle Routine currently reads as on. Extracted from
/// RoutineService so a routine shown inside a Panel gets the identical answer
/// to the same routine's dashboard tile - two surfaces disagreeing about
/// whether the floodlights are on is the bug this class exists to prevent.
///
/// Pure, like RoutineCommandMapper: the caller does the channel read and hands
/// the result in.
/// </summary>
public static class RoutineToggleState
{
    /// <summary>The channels whose latest state decides the toggle, and so the only ones a caller needs to look up.</summary>
    public static IReadOnlyList<Guid> PowerChannelIds(EfRoutine routine) =>
        routine.Actions
            .Where(a => a.Kind == RoutineActionKind.SetPower)
            .Select(a => a.ChannelId)
            .ToList();

    /// <summary>
    /// Null for a non-toggle routine, which has no on/off to report. A toggle
    /// routine is active only when every one of its power channels reads "on",
    /// so a routine with no power actions, or one whose channels have no
    /// samples yet, reads as off rather than as unknown.
    /// </summary>
    public static bool? IsActive(EfRoutine routine, IReadOnlyDictionary<Guid, ChannelLatestValue> latest)
    {
        if (!routine.IsToggle) return null;

        var powerChannelIds = PowerChannelIds(routine);
        if (powerChannelIds.Count == 0) return false;

        return powerChannelIds.All(id => latest.TryGetValue(id, out var value) && value.State == "on");
    }
}
