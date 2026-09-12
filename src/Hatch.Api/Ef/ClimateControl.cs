using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Ef;

// All four enums below store the underlying int, so new values must be
// appended at the end - see DeviceChannelMetric for what inserting elsewhere
// would do to existing rows.

/// <summary>Who asked for a command. The distinction matters at dispatch time (only Controller/Experiment defer to an active override) and at analysis time - separating what the house was told to do by a person from what it decided on its own is the whole point of the ledger.</summary>
public enum CommandSource { Human, Routine, Controller, Experiment }

/// <summary>
/// What a command does. Parallels RoutineActionKind rather than reusing it:
/// routines are one source of commands among several, and the ledger has to
/// describe a controller-issued setpoint change that no Routine was involved in.
/// </summary>
public enum CommandKind { SetPower, SetTemperature, SetHvacMode, SetFanMode, TriggerScene, PlayMedia }

/// <summary>
/// How a command ended. Rejected (failed validation here, never reached HA)
/// is deliberately distinct from Failed (HA errored) and Suppressed (an active
/// human override held it back) - when the controller starts proposing
/// actions, "the clamps refused this" and "Home Assistant was down" need
/// different responses from whoever reads the log.
/// </summary>
public enum CommandOutcome { Pending, Succeeded, Failed, Suppressed, Rejected }

/// <summary>Whether the controller may actually dispatch, or only record what it would have done. See docs/climate-brain-architecture.md Phase 4/5.</summary>
public enum ControlMode { Shadow, Live }

/// <summary>
/// One attempted actuation, recorded before it is dispatched and updated with
/// its outcome afterward. Every write to Home Assistant goes through
/// ClimateCommandService and lands here first - a command that isn't in this
/// table didn't happen.
///
/// This is the join key for everything the climate brain later learns: paired
/// against Measurement/StateChange it answers "what did we do, when, in what
/// context, and what did the house do next." That join cannot be reconstructed
/// after the fact, which is why the ledger ships before the controller does.
/// </summary>
[Table("Commands")]
[Index(nameof(ChannelId), nameof(RequestedAt))]
public class EfCommand
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid ChannelId { get; set; }
    public EfDeviceChannel? Channel { get; set; }

    public CommandKind Kind { get; set; }

    /// <summary>Interpreted per Kind, matching EfRoutineAction.Value: "true"/"false" for SetPower, a decimal string for SetTemperature, the mode string for SetHvacMode/SetFanMode, null for TriggerScene, and a media library path (not the resolved URL) for PlayMedia.</summary>
    public string? Value { get; set; }

    public CommandSource Source { get; set; }

    /// <summary>Human-readable "why", e.g. "Routine 'Night mode'" or the controller's decision rationale. Free text on purpose - it's for a person reading the log, not for querying.</summary>
    public string? Reason { get; set; }

    /// <summary>Set when this command came out of a ControlDecision, so the log can go from an actuation back to the full reasoning that produced it.</summary>
    public Guid? DecisionId { get; set; }
    public EfControlDecision? Decision { get; set; }

    public required DateTimeOffset RequestedAt { get; set; }

    /// <summary>When the HA service call was actually issued. Null for Rejected/Suppressed commands, which never reached HA.</summary>
    public DateTimeOffset? DispatchedAt { get; set; }

    public CommandOutcome Outcome { get; set; } = CommandOutcome.Pending;

    /// <summary>The HA exception message for Failed, or the validation/suppression explanation for Rejected/Suppressed.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// When the channel was next observed carrying what this command asked
    /// for. Null while unconfirmed - which is normal for the first couple of
    /// minutes (SampleChannels polls once a minute), and permanent for
    /// TriggerScene/PlayMedia, whose effects aren't a channel state to compare
    /// against. See ReconcileCommands.
    /// </summary>
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>The observed value at ConfirmedAt, as a string regardless of whether it came from a Measurement or a StateChange. Worth keeping separately from Value: a thermostat that clamps a requested 60°F to its own 62°F minimum confirms with a value we didn't ask for, and that discrepancy is exactly what the actuator-policy work later needs to see.</summary>
    public string? ConfirmedValue { get; set; }

    /// <summary>
    /// Set when this command's confirmed effect was later observed to have been
    /// undone by something outside Hatch (see EfControlOverride). Doubles as the
    /// marker that stops this command being used as an override baseline again,
    /// so one divergence produces one override row rather than a fresh one every
    /// time the reconciler runs.
    /// </summary>
    public DateTimeOffset? OverriddenAt { get; set; }
}

/// <summary>
/// One pass of the control loop: what it saw, what it chose, why, and what it
/// scored. Written whether or not anything was dispatched - in Shadow mode
/// this table is the entire output, and it stays just as interesting once the
/// controller goes Live because it's what makes a surprising action
/// explainable after the fact.
/// </summary>
[Table("ControlDecisions")]
[Index(nameof(Timestamp))]
public class EfControlDecision
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public required DateTimeOffset Timestamp { get; set; }

    public ControlMode Mode { get; set; }

    /// <summary>
    /// JSON snapshot of the zone states, effective comfort bands, and outside
    /// conditions this decision was made from. Stored as JSON rather than
    /// modeled into columns because the input set is expected to grow with the
    /// brain (occupancy, forecast, fitted coefficients), and normalizing it now
    /// would mean a migration every time it does.
    /// </summary>
    public string InputsJson { get; set; } = "{}";

    /// <summary>JSON description of the chosen action set (AC mode/setpoint, per-fan power).</summary>
    public string ChosenActionJson { get; set; } = "{}";

    /// <summary>The second-best candidate, so "why did it do that" is answerable as a diff against the closest alternative instead of a re-derivation.</summary>
    public string? RunnerUpJson { get; set; }

    public string? Reason { get; set; }

    /// <summary>The objective-function value of the chosen action - lower is better. See docs/climate-brain-architecture.md's scorer.</summary>
    public decimal? Score { get; set; }

    public List<EfCommand> Commands { get; set; } = [];
}

/// <summary>
/// A detected human override: a channel Hatch had successfully set was later
/// observed carrying something else, with no Hatch command behind the change.
///
/// This is what "single writer" means in practice - not that nothing else can
/// write to the entity, but that Hatch notices when something did and yields
/// instead of fighting it. The rows are also worth having on their own: a
/// pattern of overrides at the same hour every evening is the system telling
/// you its comfort model is wrong.
/// </summary>
[Table("ControlOverrides")]
[Index(nameof(DeviceId), nameof(SuppressedUntil))]
public class EfControlOverride
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid DeviceId { get; set; }
    public EfDevice? Device { get; set; }

    public Guid ChannelId { get; set; }
    public EfDeviceChannel? Channel { get; set; }

    /// <summary>The command whose effect was undone.</summary>
    public Guid CommandId { get; set; }
    public EfCommand? Command { get; set; }

    public required DateTimeOffset DetectedAt { get; set; }

    /// <summary>What Hatch had set the channel to, and what it was found carrying instead.</summary>
    public string? ExpectedState { get; set; }
    public string? ObservedState { get; set; }

    /// <summary>Until when the controller leaves this device alone. Human- and Routine-sourced commands are unaffected - a person telling Hatch to do something is not the same as a person overriding it.</summary>
    public required DateTimeOffset SuppressedUntil { get; set; }
}
