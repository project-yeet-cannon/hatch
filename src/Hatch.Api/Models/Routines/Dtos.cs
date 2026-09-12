using Hatch.Api.Ef;

namespace Hatch.Api.Models.Routines;

// The API-facing shapes for the Routine/RoutineAction domain. Kept separate
// from the Ef* entities so the admin app has a stable contract independent of
// storage details (same rationale as Models/DeviceMapping/Dtos.cs).

public record RoutineActionDto(Guid Id, Guid ChannelId, RoutineActionKind Kind, string? Value, int SortOrder);

public record RoutineActionWriteRequest(Guid ChannelId, RoutineActionKind Kind, string? Value, int SortOrder);

public record RoutineDto(
    Guid Id, string Name, string? Description, string? Icon, string? Color, int SortOrder, bool Included, bool IsToggle,
    IReadOnlyList<RoutineActionDto> Actions);

/// <summary>
/// A Routine's actions are embedded and replaced wholesale on write (unlike
/// Device/DeviceChannel's separate sub-resource endpoints) - the action list
/// *is* the routine, so the admin app edits it as one atomic form/request.
/// </summary>
public record RoutineWriteRequest(
    string Name, string? Description, string? Icon, string? Color, int SortOrder, bool Included, bool IsToggle,
    IReadOnlyList<RoutineActionWriteRequest> Actions);
