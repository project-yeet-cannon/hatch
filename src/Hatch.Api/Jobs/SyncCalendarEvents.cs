using Hatch.Api.Services.Calendar;
using Quartz;

namespace Hatch.Api.Jobs;

/// <summary>
/// Keeps the cached agenda fresh (docs/kiosk-architecture.md). All of the
/// work is CalendarSyncService's; this is the schedule and nothing else, the
/// same split ReconcileCommands keeps.
///
/// Five minutes is the resolution the kiosk actually needs - the panel shows
/// today and tomorrow, so an event added on a phone appears on the wall within
/// a glance or two, while three replicas' worth of firings still cost Google
/// one request per included calendar per five minutes (Quartz clustering means
/// exactly one replica runs a given firing).
/// </summary>
public class SyncCalendarEvents(ICalendarSyncService sync, ILogger<SyncCalendarEvents> logger) : IAppJob
{
    public string Name => "SyncCalendarEvents";

    public string Group => "Hatch.Api";

    public TimeSpan Interval => TimeSpan.FromMinutes(5);

    /// <summary>The only surface the cached agenda feeds is the kiosk wall, and the wall is part of the house.</summary>
    public bool ServesTheHouse => true;

    public async Task Execute(IJobExecutionContext context)
    {
        // SyncAsync is fail-soft by contract, so there is nothing to catch
        // here; the log line is what says a firing happened at all.
        var result = await sync.SyncAsync(context.CancellationToken);

        logger.LogInformation(
            "SyncCalendarEvents synced {Accounts} account(s) and {Calendars} calendar(s): {Written} event(s) written, {Removed} removed, {Failed} account(s) failed",
            result.Accounts, result.Calendars, result.Written, result.Removed, result.FailedAccounts);
    }
}
