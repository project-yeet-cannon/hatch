using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aerie.Api.Ef;

/// <summary>
/// A kiosk tile that opens a sub-UI holding several calls to action - e.g.
/// "Climate", holding the air conditioner and the two fans. Same tile geometry
/// and same name/icon/color customization as a Routine, one tier up: a Routine
/// is a single tap, a Panel is a surface you open.
///
/// A Panel holds Routines and Controls, never other Panels
/// (docs/plans/kiosk-climate.md).
/// </summary>
[Table("Panels")]
public class EfPanel
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Font Awesome solid-style icon name (e.g. "snowflake"), no "fa" prefix. Null falls back to a generic icon on the kiosk.</summary>
    public string? Icon { get; set; }

    /// <summary>Hex color (e.g. "#4b7bec") applied to the icon on the kiosk. Null falls back to the theme's ink color.</summary>
    public string? Color { get; set; }

    /// <summary>Ascending display order on the dashboard.</summary>
    public int SortOrder { get; set; }

    /// <summary>When false, the panel is hidden from the kiosk dashboard but still editable from admin.</summary>
    public bool Included { get; set; } = true;

    public List<EfPanelItem> Items { get; set; } = [];
}

// New values must be appended at the end - the column stores the enum's
// underlying int, see DeviceChannelMetric for why.
public enum PanelItemKind { Routine, Control }

// New values must be appended at the end - the column stores the enum's
// underlying int, see DeviceChannelMetric for why. Light and Camera controls
// are an append here plus a kiosk component, not a redesign.
public enum ControlKind { Switch, Thermostat }

// New values must be appended at the end - the column stores the enum's
// underlying int, see DeviceChannelMetric for why.
public enum ControlRole { Power, Setpoint, Mode, Ambient }

/// <summary>
/// One entry in a Panel's ordered list: either an existing Routine, or a
/// Control - a typed, device-bound surface that both reports current state and
/// writes a parametric value, which a Routine cannot do.
///
/// One table with a discriminator rather than two parallel tables, because what
/// the kiosk renders is one ordered list and <see cref="SortOrder"/> has to be
/// coherent across both kinds. The nullable columns are that choice's cost;
/// each one documents which Kind makes it non-null.
/// </summary>
[Table("PanelItems")]
public class EfPanelItem
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid PanelId { get; set; }
    public EfPanel? Panel { get; set; }

    /// <summary>Display order within the Panel, across both kinds.</summary>
    public int SortOrder { get; set; }

    public PanelItemKind Kind { get; set; }

    /// <summary>The Routine this item runs. Non-null iff Kind == Routine; null for a Control.</summary>
    public Guid? RoutineId { get; set; }
    public EfRoutine? Routine { get; set; }

    /// <summary>Which control surface to render. Non-null iff Kind == Control; null for a Routine item.</summary>
    public ControlKind? ControlKind { get; set; }

    /// <summary>Control display name on the kiosk (e.g. "Air conditioner"). Null for a Routine item, which uses the Routine's own Name.</summary>
    public string? Label { get; set; }

    /// <summary>Font Awesome solid-style icon name for a Control. Null for a Routine item, which uses the Routine's own Icon.</summary>
    public string? Icon { get; set; }

    /// <summary>Hex color for a Control. Null for a Routine item, which uses the Routine's own Color.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Thermostat only: the HVAC mode that means "on" for this device - "cool"
    /// for the air conditioner, "heat" for a radiator. Storing it per control is
    /// what lets both present the single On/Off the kiosk shows. Null for every
    /// other kind, and null on a Thermostat that has a Power binding instead
    /// (see PanelBindingRules).
    /// </summary>
    public string? OnMode { get; set; }

    /// <summary>Thermostat only: lowest settable setpoint in F. Null falls back to PanelDefaults.MinF.</summary>
    public decimal? MinF { get; set; }

    /// <summary>Thermostat only: highest settable setpoint in F. Null falls back to PanelDefaults.MaxF.</summary>
    public decimal? MaxF { get; set; }

    /// <summary>Thermostat only: increment per +/- tap in F. Null falls back to PanelDefaults.StepF.</summary>
    public decimal? StepF { get; set; }

    /// <summary>The channels this Control reads and writes, one per role. Empty for a Routine item.</summary>
    public List<EfPanelControlBinding> Bindings { get; set; } = [];
}

/// <summary>
/// One (role, channel) pair on a Control - e.g. a Thermostat's Setpoint role
/// bound to the AC's SetpointTemperature channel. Which roles a kind requires,
/// and which metric each role demands, is PanelBindingRules' business; this
/// table just stores the answer.
/// </summary>
[Table("PanelControlBindings")]
public class EfPanelControlBinding
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ItemId { get; set; }
    public EfPanelItem? Item { get; set; }

    public ControlRole Role { get; set; }

    public Guid ChannelId { get; set; }
    public EfDeviceChannel? Channel { get; set; }
}
