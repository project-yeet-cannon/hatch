using Quartz;

namespace Aerie.Api.Jobs;

public interface IAerieJob : IJob
{
    string Name { get; }
    string Group { get; }
    TimeSpan Interval { get; }
}

public class JobsInit(IScheduler scheduler, IEnumerable<IAerieJob> jobs)
{
    /// <summary>
    /// Registers every recurring job, replacing whatever is already there.
    /// Quartz clustering handles execution (only one node in the cluster runs
    /// a given firing), not registration - every replica calls this on every
    /// boot, so `replace: true` is load-bearing: without it, two replicas
    /// racing between "does this job exist" and "create it" both win the
    /// race and the loser throws ObjectAlreadyExistsException and
    /// crash-loops.
    /// </summary>
    public async Task WireUpJobs()
    {
        foreach (var j in jobs)
        {
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
    }

    /// <summary>
    /// Registers a durable job with no trigger - it stays dormant until
    /// something calls IScheduler.TriggerJob(jobKey, dataMap) for it, unlike
    /// the recurring IAerieJobs wired up by WireUpJobs. For jobs like
    /// BackfillChannelHistory that run on demand with caller-supplied data
    /// rather than on a fixed interval.
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
