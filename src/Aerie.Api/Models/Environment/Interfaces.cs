namespace Aerie.Api.Models.Environment;

public interface IEnvironmentService
{
    Task<IEnumerable<EnvironmentReading>> FetchFromHomeAssistant();

    Task<IReadOnlyCollection<EnvironmentReading>> GetReadings();
    Task BulkInsertReadings(IEnumerable<EnvironmentReading> readings);
}
