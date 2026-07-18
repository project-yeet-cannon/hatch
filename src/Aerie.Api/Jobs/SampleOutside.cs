using Aerie.Api.Models.Environment;
using Aerie.Api.Services.Dashboard;
using Microsoft.Extensions.Options;
using Quartz;

namespace Aerie.Api.Jobs;

/// <summary>
/// Samples the configured outdoor temperature/humidity sensor.* entities into
/// EnvironmentReadings, mirroring SampleEnvironments for climate.* zones so
/// WeatherService has history to chart.
/// </summary>
public class SampleOutside(TimeProvider t, IEnvironmentService es, IOptions<DashboardOptions> options) : IAerieJob
{
    public string Name => "SampleOutside";

    public string Group => "Aerie.Api";

    public TimeSpan Interval => TimeSpan.FromMinutes(1);

    public async Task Execute(IJobExecutionContext context)
    {
        var opt = options.Value;
        var now = t.GetUtcNow();
        var then = now.Subtract(Interval * 2);

        var readings = new List<EnvironmentReading>();
        if (!string.IsNullOrWhiteSpace(opt.OutsideTemperatureEntity))
            readings.AddRange(await es.FetchSensorHistoryFromHomeAssistant(opt.OutsideTemperatureEntity, then, now, isTemperature: true));
        if (!string.IsNullOrWhiteSpace(opt.OutsideHumidityEntity))
            readings.AddRange(await es.FetchSensorHistoryFromHomeAssistant(opt.OutsideHumidityEntity, then, now, isTemperature: false));

        if (readings.Count > 0)
            await es.BulkInsertReadings(readings);
    }
}
