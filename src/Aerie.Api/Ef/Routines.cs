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

    /// <summary>Ascending display order on the dashboard.</summary>
    public int SortOrder { get; set; }

    /// <summary>When false, the routine is hidden from the kiosk dashboard but still triggerable/editable from admin.</summary>
    public bool Included { get; set; } = true;

    public List<EfRoutineAction> Actions { get; set; } = [];
}

// New values must be appended at the end - the column stores the enum's
// underlying int, see DeviceChannelMetric for why.
public enum RoutineActionKind { SetPower, SetTemperature, SetHvacMode, SetFanMode, TriggerScene }

/// <summary>
/// One command in a Routine, executed against a DeviceChannel's underlying HA
/// entity via IHomeAssistantCommandService - mirrors DevicesController's
/// channel-write endpoints exactly, just batched and named.
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
    /// for TriggerScene.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>Execution and display order within the Routine.</summary>
    public int SortOrder { get; set; }
}
