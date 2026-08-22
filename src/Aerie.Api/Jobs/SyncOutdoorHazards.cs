using Aerie.Api.Services.Hazards;
using Quartz;

namespace Aerie.Api.Jobs;

/// <summary>
/// Keeps the cached weather alerts and air quality samples fresh
/// (docs/plans/kiosk.md phase B4). All of the work is HazardSyncService's;
/// this is the schedule and nothing else, the same split SyncCalendarEvents
/// keeps.
///
/// One job for both providers even though their natural cadences differ: a
/// second job to save a keyless HTTP call every half hour is machinery for its
/// own sake. Fifteen minutes is what a warning is worth - fast enough that a
/// tornado warning is on the wall while it matters, slow enough to be a polite
/// neighbor to a free, unauthenticated API (Quartz clustering means exactly one
/// replica runs a given firing).
/// </summary>
public class SyncOutdoorHazards(IHazardSyncService sync, ILogger<SyncOutdoorHazards> logger) : IAerieJob
{
    public string Name => "SyncOutdoorHazards";

    public string Group => "Aerie.Api";

    public TimeSpan Interval => TimeSpan.FromMinutes(15);

    public async Task Execute(IJobExecutionContext context)
    {
        // SyncAsync is fail-soft by contract, so there is nothing to catch
        // here; the log line is what says a firing happened at all.
        var result = await sync.SyncAsync(context.CancellationToken);

        logger.LogInformation(
            "SyncOutdoorHazards wrote {Written} alert(s), deactivated {Deactivated}, and stored {Samples} air quality sample(s) (weather failed: {WeatherFailed}, air quality failed: {AirQualityFailed})",
            result.AlertsWritten, result.AlertsDeactivated, result.SamplesWritten, result.WeatherFailed, result.AirQualityFailed);
    }
}
