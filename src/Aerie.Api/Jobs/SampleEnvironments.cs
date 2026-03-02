using Aerie.Api.Models.Environment;
using Quartz;

namespace Aerie.Api.Jobs;

public class SampleEnvironments(IEnvironmentService es) : IAerieJob
{
    public string Name => "SampleEnvironments";

    public string Group => "Aerie.Api";

    public async Task Execute(IJobExecutionContext context)
    {
        Console.WriteLine("stuff " + DateTime.Now.ToLongTimeString());
        await Task.Delay(100);
        // var readings = await es.FetchAllFromHomeAssistant("climate.");
        // await es.BulkInsertReadings(readings);
    }
}
