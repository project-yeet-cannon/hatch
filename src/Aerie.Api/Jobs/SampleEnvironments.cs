using Aerie.Api.Models.Environment;
using Quartz;

namespace Aerie.Api.Jobs;

public class SampleEnvironments(TimeProvider t, IEnvironmentService es) : IAerieJob
{
    public string Name => "SampleEnvironments";

    public string Group => "Aerie.Api";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task Execute(IJobExecutionContext context)
    {
        var now = t.GetUtcNow();
        var then = now.Subtract(Interval * 2);

        var readings = await es.FetchAllFromHomeAssistant("climate.", then, now);
        await es.BulkInsertReadings(readings);
    }
}
