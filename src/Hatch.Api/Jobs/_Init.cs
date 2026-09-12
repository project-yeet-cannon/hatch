using Hatch.Api.Services.DeviceMapping;
using Quartz;

namespace Hatch.Api.Jobs;

public interface IAppJob : IJob
{
    string Name { get; }
    string Group { get; }
    TimeSpan Interval { get; }

    /// <summary>
    /// Whether this job serves the house half of Hatch - the half that only
    /// exists when a Home Assistant connection does. Home Assistant is what
    /// makes an installation a house: the zones, devices, channels, climate and
    /// motion are all Home-Assistant-shaped, and the kiosk wall the calendar
    /// agenda and the hazard panel are drawn on does not exist without one. A
    /// job that says true here is not scheduled on an install with no
    /// connection, and is unscheduled when one is cleared.
    /// </summary>
    /// <remarks>
    /// No default implementation, on purpose. A default is what would silently
    /// swallow the next job somebody adds - it would inherit whichever answer
    /// this file guessed, and nobody would be asked. An interface member with
    /// no default makes the author answer the question in one line.
    /// </remarks>
    bool ServesTheHouse { get; }
}

public class JobsInit(
    IScheduler scheduler,
    IEnumerable<IAppJob> jobs,
    IHomeAssistantConnectionManager haConnection,
    ILogger<JobsInit> logger)
{
    /// <summary>
    /// Registers every recurring job, replacing whatever is already there.
    /// Quartz clustering handles execution (only one node in the cluster runs
    /// a given firing), not registration - every replica calls this on every
    /// boot, so `replace: true` is load-bearing: without it, two replicas
    /// racing between "does this job exist" and "create it" both win the
    /// race and the loser throws ObjectAlreadyExistsException and
    /// crash-loops.
    ///
    /// <para>The house jobs are scheduled only while a Home Assistant
    /// connection resolves, and deleted when one does not - so an install with
    /// no house schedules nothing rather than running four jobs forever against
    /// a house that is not there. Because this is also called by
    /// SettingsController on both sides of the connection's life, and because
    /// the Quartz store is shared and clustered, saving a connection starts
    /// them across every replica and clearing one stops them, with no restart.
    /// Idempotent by construction - `replace: true` and DeleteJob's tolerance
    /// of a key that is not there - which matters because the settings are
    /// saved one key at a time and this runs once per key.</para>
    /// </summary>
    public async Task WireUpJobs()
    {
        var house = await ResolveHouseAsync();

        foreach (var j in jobs)
        {
            if (j.ServesTheHouse && house is null)
            {
                await scheduler.DeleteJob(new JobKey(j.Name, j.Group));
                continue;
            }

            var qj = JobBuilder.Create(j.GetType())
                .WithIdentity(j.Name, j.Group)
                .Build();

            var qt = TriggerBuilder.Create()
                .WithIdentity($"{j.Name}_Trigger", j.Group)
                .WithSimpleSchedule(s => s
                    .WithInterval(j.Interval)
                    .RepeatForever())
                .StartNow()
                .Build();

            await scheduler.ScheduleJob(qj, [qt], replace: true);
        }

        if (house is null)
            logger.LogInformation(
                "No Home Assistant connection is configured, so the house jobs are not scheduled. "
                + "Save a host, port and token on the admin Settings page to start them - no restart needed.");
    }

    /// <summary>
    /// The connection, or null for both of the ways there can be no house: none
    /// is configured, and the settings could not be read at all. The second is
    /// the database nobody has migrated yet, and it reaches here on the startup
    /// path - so it is "no house this start", said once, rather than an
    /// exception out of Main. Narrow on purpose: it covers this one call and
    /// nothing below it, and the health check still reports the database
    /// honestly.
    /// </summary>
    private async Task<HomeAssistantConnection?> ResolveHouseAsync()
    {
        try
        {
            return await haConnection.ResolveAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the site settings; treating Home Assistant as unconfigured and scheduling no house jobs.");
            return null;
        }
    }

    /// <summary>
    /// Registers a durable job with no trigger - it stays dormant until
    /// something calls IScheduler.TriggerJob(jobKey, dataMap) for it, unlike
    /// the recurring IAppJobs wired up by WireUpJobs. For jobs like
    /// BackfillChannelHistory that run on demand with caller-supplied data
    /// rather than on a fixed interval.
    ///
    /// <para>Unconditional even on an install with no house: it has no trigger,
    /// so it costs one row and fires nothing.</para>
    /// </summary>
    public async Task WireUpTriggerableJob<TJob>(string name, string group) where TJob : IJob
    {
        var jd = JobBuilder.Create<TJob>()
            .WithIdentity(name, group)
            .StoreDurably()
            .Build();

        await scheduler.AddJob(jd, replace: true);
    }
}
