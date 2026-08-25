using Aerie.Api.Ef;

namespace Aerie.Api.Models.Panels;

// The API-facing shapes for the Panel/PanelItem/PanelControlBinding domain.
// Kept separate from the Ef* entities so the admin and kiosk apps have a stable
// contract independent of storage details (same rationale as
// Models/Routines/Dtos.cs and Models/DeviceMapping/Dtos.cs).
//
// Three groups live here, and they answer different questions:
//   - PanelSummary          the kiosk tile, carried on the dashboard snapshot
//   - PanelDto and friends  the admin's editable shape, and its write mirror
//   - PanelStateDto         live per-item state, polled while the overlay is open

/// <summary>
/// A Panel as the kiosk's tile row renders it - id, name, icon, color, and
/// nothing else. Deliberately not the items: GET /api/dashboard is a 60s poll
/// carrying every panel whether or not anyone opened one, and live control
/// state is both too stale at that interval and too expensive to gather
/// unconditionally. The overlay fetches its own state (see PanelStateDto).
/// </summary>
public record PanelSummary(Guid Id, string Name, string? Icon, string? Color);

public record PanelControlBindingDto(Guid Id, ControlRole Role, Guid ChannelId);

/// <summary>
/// One item in a Panel's ordered list, in the admin's editable shape. The
/// nullable fields mirror <see cref="EfPanelItem"/>'s exactly, including which
/// Kind makes each one non-null - the DTO is a projection of the entity, not a
/// second opinion about it.
/// </summary>
public record PanelItemDto(
    Guid Id,
    int SortOrder,
    PanelItemKind Kind,
    Guid? RoutineId,
    ControlKind? ControlKind,
    string? Label,
    string? Icon,
    string? Color,
    string? OnMode,
    decimal? MinF,
    decimal? MaxF,
    decimal? StepF,
    IReadOnlyList<PanelControlBindingDto> Bindings);

public record PanelDto(
    Guid Id, string Name, string? Description, string? Icon, string? Color, int SortOrder, bool Included,
    IReadOnlyList<PanelItemDto> Items);

public record PanelControlBindingWriteRequest(ControlRole Role, Guid ChannelId);

public record PanelItemWriteRequest(
    int SortOrder,
    PanelItemKind Kind,
    Guid? RoutineId,
    ControlKind? ControlKind,
    string? Label,
    string? Icon,
    string? Color,
    string? OnMode,
    decimal? MinF,
    decimal? MaxF,
    decimal? StepF,
    IReadOnlyList<PanelControlBindingWriteRequest> Bindings);

/// <summary>
/// A Panel's items are embedded and replaced wholesale on write, exactly as
/// RoutineWriteRequest does with its actions - the item list *is* the panel, and
/// SortOrder has to stay coherent across both item kinds, which it cannot if the
/// two halves arrive in separate requests.
/// </summary>
public record PanelWriteRequest(
    string Name, string? Description, string? Icon, string? Color, int SortOrder, bool Included,
    IReadOnlyList<PanelItemWriteRequest> Items);

/// <summary>
/// Live state for one item in an open Panel overlay. One shape for both kinds,
/// because the kiosk renders one ordered list: the control fields are null on a
/// routine item and the routine fields are null on a control.
/// </summary>
/// <param name="Label">What to print on the control. A routine item gets the Routine's own Name here, so the kiosk never has to look in two places for a label.</param>
/// <param name="IsOn">Null when the control has no on/off at all (a setpoint-only thermostat) *and* when it has one that has never reported - an unread channel is unknown, not off. Contrast <paramref name="IsActive"/>.</param>
/// <param name="Mode">The HvacMode channel's raw state ("cool", "off", ...) when one is bound. Reported as-is rather than compared to OnMode, so the kiosk can show what the device actually says.</param>
/// <param name="MinF">Resolved against PanelDefaults, not the raw column - the kiosk needs a number to clamp against, and "null means 60" is a rule the server already knows. Null on a Switch, which has no bounds.</param>
/// <param name="IsToggle">Null on a control; a control's on/off lives in <paramref name="IsOn"/>.</param>
/// <param name="IsActive">Null on a control and on a momentary routine. A toggle routine with no samples yet reads false, not null, because that is what RoutineService's dashboard tile reads and the two surfaces must agree.</param>
public record PanelItemStateDto(
    Guid Id,
    PanelItemKind Kind,
    string? Label,
    string? Icon,
    string? Color,
    ControlKind? ControlKind,
    bool? IsOn,
    decimal? SetpointF,
    decimal? AmbientF,
    string? Mode,
    decimal? MinF,
    decimal? MaxF,
    decimal? StepF,
    bool? IsToggle,
    bool? IsActive);

public record PanelStateDto(Guid Id, string Name, IReadOnlyList<PanelItemStateDto> Items);

/// <summary>The kiosk's on/off tap. Absolute rather than a toggle: the wall's copy of the state can be up to a poll stale, and a toggle would then do the opposite of what the finger asked for.</summary>
public record PanelPowerRequest(bool On);

/// <summary>The kiosk's setpoint write, in F. One request per settled interaction - holding "+" from 68 to 78 sends this once, not ten times (docs/plans/kiosk-climate.md).</summary>
public record PanelSetpointRequest(decimal ValueF);
