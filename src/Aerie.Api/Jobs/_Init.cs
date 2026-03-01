using System.Collections.Specialized;
using Quartz;

namespace Aerie.Api.Jobs;

public static class JobsInit
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
}
