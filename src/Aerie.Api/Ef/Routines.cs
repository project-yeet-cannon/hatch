using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Aerie.Api.Ef;

/// <summary>
/// A named, manually-triggered bundle of device actions - e.g. "Night mode" or
/// "Max AC". Broader than a Scene (which is just a bare HA scene.* entity, see
/// DeviceChannelMetric.Scene): a Routine can span any number of devices/channels
/// and any mix of action kinds.
/// </summary>
[Table("Routines")]
public class EfRoutine
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>Font Awesome solid-style icon name (e.g. "lightbulb"), no "fa" prefix. Null falls back to a generic icon on the kiosk.</summary>
    public string? Icon { get; set; }

    /// <summary>Hex color (e.g. "#4b7bec") applied to the icon on the kiosk. Null falls back to the theme's ink color.</summary>
    public string? Color { get; set; }

    /// <summary>Ascending display order on the dashboard.</summary>
    public int SortOrder { get; set; }

    /// <summary>When false, the routine is hidden from the kiosk dashboard but still triggerable/editable from admin.</summary>
    public bool Included { get; set; } = true;

    public List<EfRoutineAction> Actions { get; set; } = [];
}

// New values must be appended at the end - the column stores the enum's
// underlying int, see DeviceChannelMetric for why.
public enum RoutineActionKind { SetPower, SetTemperature, SetHvacMode, SetFanMode, TriggerScene, PlayMedia }

/// <summary>
/// One command in a Routine, translated into a ledgered CommandRequest by
/// RoutineCommandMapper and dispatched through IClimateCommandService - mirrors
/// DevicesController's channel-write endpoints exactly, just batched and named.
/// </summary>
[Table("RoutineActions")]
public class EfRoutineAction
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid RoutineId { get; set; }
    public EfRoutine? Routine { get; set; }

    public Guid ChannelId { get; set; }
    public EfDeviceChannel? Channel { get; set; }

    public RoutineActionKind Kind { get; set; }

    /// <summary>
    /// Interpreted per Kind: "true"/"false" for SetPower, a decimal string for
    /// SetTemperature, the mode string for SetHvacMode/SetFanMode, null/ignored
    /// for TriggerScene, and a media library path (or URL, or media-source id)
    /// for PlayMedia.
    ///
    /// PlayMedia deliberately stores the library-relative path rather than the
    /// resolved URL: MediaLibraryBaseUrl changes with hostnames and proxy
    /// layout, and a routine saved last year shouldn't still be pointing at
    /// the URL that was current then. It's resolved at execution time - see
    /// RoutineActionExecutor.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>Execution and display order within the Routine.</summary>
    public int SortOrder { get; set; }
}
