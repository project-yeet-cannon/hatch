namespace Aerie.Api.Models.Environment;

public interface IEnvironmentService
{
    Task<IEnumerable<EnvironmentReading>> FetchFromHomeAssistant(string entityName);
    Task<IEnumerable<EnvironmentReading>> FetchAllFromHomeAssistant(string namePrefix);

    Task<IReadOnlyCollection<EnvironmentReading>> GetReadings();
    Task BulkInsertReadings(IEnumerable<EnvironmentReading> readings);
}
