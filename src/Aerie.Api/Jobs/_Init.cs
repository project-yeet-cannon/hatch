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
    public async Task WireUpJobs()
    {
        foreach (var j in jobs)
        {
            var jd = await scheduler.GetJobDetail(JobKey.Create(j.Name, j.Group));
            if (jd is not null)
            {
                // TODO handle update case
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

            await scheduler.ScheduleJob(qj, qt);
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
        if (await scheduler.CheckExists(JobKey.Create(name, group)))
        {
            return;
        }

        var jd = JobBuilder.Create<TJob>()
            .WithIdentity(name, group)
            .StoreDurably()
            .Build();

        await scheduler.AddJob(jd, replace: false);
    }
}
