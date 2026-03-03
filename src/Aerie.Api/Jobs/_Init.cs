using System.Collections.Specialized;
using Quartz;

namespace Aerie.Api.Jobs;

public interface IAerieJob : IJob
{
    string Name { get; }
    string Group { get; }
    TimeSpan Interval { get; }
}

public class JobsInit(ISchedulerFactory sf, IEnumerable<IAerieJob> jobs)
{
    public static async Task<IScheduler> InitQuartz(string psqlCxnStr)
    {
        var properties = new NameValueCollection();
        var sch = await SchedulerBuilder.Create(properties)
            .UsePersistentStore(sb =>
            {
                sb.UseProperties = true;
                sb.UseClustering();
                sb.UsePostgres(psqlCxnStr);
                sb.UseSystemTextJsonSerializer();
            })
            .BuildScheduler();

        return sch;
    }

    public async Task WireUpJobs()
    {
        var sch = await sf.GetScheduler();

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

            await sch.ScheduleJob(qj, qt);
        }
    }
}
