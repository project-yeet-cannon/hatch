using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Media;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.ClimateControl;

/// <summary>One requested actuation. Value follows EfCommand.Value's per-Kind interpretation - notably, PlayMedia carries the media library path, not a resolved URL.</summary>
public readonly record struct CommandRequest(
    Guid ChannelId, CommandKind Kind, string? Value, CommandSource Source, string? Reason, Guid? DecisionId = null);

/// <summary>The ledger row's id plus how it ended, so callers can map an outcome onto an HTTP status without re-reading the table.</summary>
public readonly record struct CommandResult(Guid CommandId, CommandOutcome Outcome, string? Error)
{
    public bool Succeeded => Outcome == CommandOutcome.Succeeded;
}

public interface IClimateCommandService
{
    Task<CommandResult> DispatchAsync(CommandRequest request, CancellationToken ct);

    /// <summary>Dispatches in the given order, sequentially, stopping at the first non-success. Sequential because HA shouldn't be hit with a burst of concurrent service calls and because a routine's order is meaningful ("radiators off, then AC down, then fans on").</summary>
    Task<IReadOnlyList<CommandResult>> DispatchManyAsync(IReadOnlyList<CommandRequest> requests, CancellationToken ct);
}

/// <summary>
/// The single chokepoint through which every write to Home Assistant passes:
/// record the intent, check it's legal, check nobody has overridden the device,
/// dispatch, record the outcome. Nothing else in the app calls
/// IHomeAssistantCommandService directly - that's what makes the Commands table
/// a complete account of what Aerie has done to the house rather than a partial
/// one, and it's why the later actuator-policy clamps
/// (docs/climate-brain-architecture.md Phase 2) belong here too: routines, the
/// admin UI, the control loop, and experiments all inherit them without each
/// remembering to ask.
/// </summary>
public class ClimateCommandService(
    AerieContext db,
    IHomeAssistantCommandService ha,
    ISiteSettingsService siteSettings,
    TimeProvider time,
    ILogger<ClimateCommandService> logger) : IClimateCommandService
{
    public async Task<IReadOnlyList<CommandResult>> DispatchManyAsync(IReadOnlyList<CommandRequest> requests, CancellationToken ct)
    {
        var results = new List<CommandResult>(requests.Count);
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            var result = await DispatchAsync(request, ct);
            results.Add(result);
            if (!result.Succeeded) break;
        }
        return results;
    }

    public async Task<CommandResult> DispatchAsync(CommandRequest request, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.AsNoTracking()
            .Include(c => c.Device)
            .FirstOrDefaultAsync(c => c.Id == request.ChannelId, ct);

        if (channel is null)
            throw new InvalidOperationException($"No DeviceChannel {request.ChannelId} to command.");

        var command = new EfCommand
        {
            ChannelId = channel.Id,
            Kind = request.Kind,
            Value = request.Value,
            Source = request.Source,
            Reason = request.Reason,
            DecisionId = request.DecisionId,
            RequestedAt = time.GetUtcNow(),
        };

        if (CommandExpectation.Validate(channel, request.Kind, request.Value) is { } rejection)
            return await FinishAsync(command, CommandOutcome.Rejected, rejection, ct);

        if (await SuppressionReasonAsync(channel, request.Source, ct) is { } suppression)
            return await FinishAsync(command, CommandOutcome.Suppressed, suppression, ct);

        // Persisted before the call goes out, so a crash or an HA timeout mid-flight
        // still leaves evidence that Aerie tried - an actuation that vanished from
        // the record is worse than one recorded as Pending forever.
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);

        try
        {
            await SendAsync(channel, request, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command {CommandId} ({Kind} on {EntityId}) failed", command.Id, request.Kind, channel.HaEntityId);
            command.DispatchedAt = time.GetUtcNow();
            command.Outcome = CommandOutcome.Failed;
            command.Error = ex.Message;
            await db.SaveChangesAsync(ct);
            return new CommandResult(command.Id, CommandOutcome.Failed, ex.Message);
        }

        command.DispatchedAt = time.GetUtcNow();
        command.Outcome = CommandOutcome.Succeeded;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Command {CommandId}: {Source} {Kind} {Value} on {EntityId} ({Reason})",
            command.Id, request.Source, request.Kind, request.Value, channel.HaEntityId, request.Reason);

        return new CommandResult(command.Id, CommandOutcome.Succeeded, null);
    }

    private async Task SendAsync(EfDeviceChannel channel, CommandRequest request, CancellationToken ct)
    {
        var entityId = channel.HaEntityId;
        switch (request.Kind)
        {
            case CommandKind.SetPower:
                await ha.SetPowerAsync(entityId, bool.Parse(request.Value!));
                break;
            case CommandKind.SetTemperature:
                await ha.SetTemperatureAsync(entityId, decimal.Parse(request.Value!, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case CommandKind.SetHvacMode:
                await ha.SetHvacModeAsync(entityId, request.Value!);
                break;
            case CommandKind.SetFanMode:
                await ha.SetFanModeAsync(entityId, request.Value!);
                break;
            case CommandKind.TriggerScene:
                await ha.TriggerSceneAsync(entityId);
                break;
            case CommandKind.PlayMedia:
                await ha.PlayMediaAsync(entityId, await ResolveMediaAsync(request.Value!, ct), MediaContentTypes.DefaultPlayMediaType);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unknown CommandKind.");
        }
    }

    /// <summary>
    /// Resolved here at dispatch time rather than stored resolved - see
    /// EfRoutineAction.Value. MediaLibraryBaseUrl changes with hostnames and
    /// proxy layout, so the ledger keeps the library-relative path and the URL
    /// is rebuilt against whatever the base URL is now.
    /// </summary>
    private async Task<string> ResolveMediaAsync(string value, CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var (url, error) = MediaLibraryUrlResolver.Resolve(value, settings.MediaLibraryBaseUrl);
        return url ?? throw new InvalidOperationException($"Can't play '{value}': {error}");
    }

    /// <summary>
    /// Only the autonomous sources defer to an override. A person pressing a
    /// button in the admin UI, or triggering a routine, is telling Aerie what
    /// to do - which is the opposite of the situation an override describes,
    /// and refusing them would make the backoff feel like the app was broken.
    /// </summary>
    private async Task<string?> SuppressionReasonAsync(EfDeviceChannel channel, CommandSource source, CancellationToken ct)
    {
        if (source is not (CommandSource.Controller or CommandSource.Experiment)) return null;
        if (channel.Device is null) return null;

        var now = time.GetUtcNow();
        var active = await db.ControlOverrides.AsNoTracking()
            .Where(o => o.DeviceId == channel.Device.Id && o.SuppressedUntil > now)
            .OrderByDescending(o => o.SuppressedUntil)
            .FirstOrDefaultAsync(ct);

        return active is null
            ? null
            : $"Device is under a manual override detected at {active.DetectedAt:O}; deferring until {active.SuppressedUntil:O}.";
    }

    /// <summary>Records a command that never reached HA. Rejected/Suppressed rows are kept rather than dropped: an action the guards refused is exactly what someone debugging the controller needs to see.</summary>
    private async Task<CommandResult> FinishAsync(EfCommand command, CommandOutcome outcome, string error, CancellationToken ct)
    {
        command.Outcome = outcome;
        command.Error = error;
        db.Commands.Add(command);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Command {CommandId} {Outcome}: {Error}", command.Id, outcome, error);
        return new CommandResult(command.Id, outcome, error);
    }
}
