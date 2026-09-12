using Hatch.Api.Ef;
using Hatch.Api.Services.ClimateControl;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Hatch.Api.Jobs;

/// <summary>
/// Closes the loop on the command ledger (docs/climate-brain-architecture.md
/// Phase 1) by comparing what Hatch asked for against what SampleChannels
/// subsequently observed, doing two things:
///
/// 1. **Confirmation** - marks a command confirmed once its channel is seen
///    carrying the commanded value, recording the observed value separately
///    from the requested one so a thermostat clamping a request to its own
///    limits shows up as the discrepancy it is.
/// 2. **Override detection** - once a command has been confirmed, a later
///    observation that no longer matches it, with no Hatch command behind the
///    change, means a person turned the knob. That records an EfControlOverride
///    and backs the controller off the device for a while.
///
/// Deliberately conservative: a command that was never confirmed is never
/// treated as an override baseline. Without having seen our own value in place
/// first, "the channel doesn't match what we asked for" is just as likely to
/// mean the command didn't take as it is to mean somebody countermanded it,
/// and inventing overrides out of the former would have the controller
/// disabling itself for reasons nobody could explain.
/// </summary>
public class ReconcileCommands(
    TimeProvider time, AppDbContext db, ISiteSettingsService siteSettings, ILogger<ReconcileCommands> logger) : IAppJob
{
    public string Name => "ReconcileCommands";

    public string Group => "Hatch.Api";

    /// <summary>Twice SampleChannels' interval - there's nothing to reconcile against until a fresh sample has landed, so running faster would just re-read the same rows.</summary>
    public TimeSpan Interval => TimeSpan.FromMinutes(2);

    /// <summary>It reconciles commands sent to Home Assistant devices against what SampleChannels observed; with no connection there are neither.</summary>
    public bool ServesTheHouse => true;

    public Task Execute(IJobExecutionContext context) => ReconcileAsync(context.CancellationToken);

    /// <summary>The job's actual work, separated from Quartz's IJobExecutionContext so it can be driven directly from tests.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var settings = await siteSettings.GetAsync(ct);
        var backoff = TimeSpan.FromMinutes(settings.OverrideBackoffMinutes);

        var channelIds = await db.Commands.AsNoTracking()
            .Where(c => c.Outcome == CommandOutcome.Succeeded)
            .Select(c => c.ChannelId)
            .Distinct()
            .ToListAsync(ct);

        if (channelIds.Count == 0)
        {
            logger.LogInformation("ReconcileCommands: no dispatched commands to reconcile");
            return;
        }

        var latest = await ChannelLatestValues.GetLatestAsync(db, channelIds, ct);
        var confirmed = 0;
        var overridden = 0;

        foreach (var channelId in channelIds)
        {
            ct.ThrowIfCancellationRequested();

            // Only the most recent dispatched command matters - an older one has
            // been superseded, and confirming it now would say nothing about the
            // channel's current state.
            var command = await db.Commands
                .Where(c => c.ChannelId == channelId && c.Outcome == CommandOutcome.Succeeded)
                .OrderByDescending(c => c.RequestedAt)
                .FirstOrDefaultAsync(ct);

            if (command is null) continue;
            if (CommandExpectation.For(command.Kind, command.Value) is not { } expected) continue;

            var observed = latest.GetValueOrDefault(channelId);
            if (observed.Timestamp is null) continue;

            var matches = CommandExpectation.Matches(expected, observed);

            if (command.ConfirmedAt is null)
            {
                // Intentionally not requiring the observation to postdate
                // dispatch: commanding a channel to the value it already holds
                // is a legitimate no-op, and refusing to confirm it would leave
                // that channel without an override baseline until something
                // happened to change its state.
                if (!matches) continue;

                command.ConfirmedAt = observed.Timestamp;
                command.ConfirmedValue = CommandExpectation.Render(observed);
                confirmed++;
                logger.LogInformation("Command {CommandId} confirmed at {ConfirmedAt:O} with {Value}",
                    command.Id, command.ConfirmedAt, command.ConfirmedValue);
            }
            else if (command.OverriddenAt is null && !matches && observed.Timestamp > command.ConfirmedAt)
            {
                var device = await db.DeviceChannels.AsNoTracking()
                    .Where(c => c.Id == channelId)
                    .Select(c => c.DeviceId)
                    .FirstOrDefaultAsync(ct);

                db.ControlOverrides.Add(new EfControlOverride
                {
                    DeviceId = device,
                    ChannelId = channelId,
                    CommandId = command.Id,
                    DetectedAt = now,
                    ExpectedState = expected.ToString(),
                    ObservedState = CommandExpectation.Render(observed),
                    SuppressedUntil = now + backoff,
                });

                // Marking the command overridden retires it as a baseline, so
                // one divergence produces one override row instead of a fresh
                // one on every pass until the controller happens to command the
                // channel again.
                command.OverriddenAt = now;
                overridden++;

                logger.LogWarning(
                    "Manual override detected on channel {ChannelId}: expected {Expected}, observed {Observed}. Suppressing device {DeviceId} until {Until:O}",
                    channelId, expected, CommandExpectation.Render(observed), device, now + backoff);
            }
        }

        if (confirmed > 0 || overridden > 0) await db.SaveChangesAsync(ct);

        logger.LogInformation("ReconcileCommands: checked {ChannelCount} channels, confirmed {Confirmed}, detected {Overridden} overrides",
            channelIds.Count, confirmed, overridden);
    }
}
