namespace Aerie.Api.Models.Environment;

public interface IEnvironmentService
{
    Task<IEnumerable<EnvironmentReading>> FetchFromHomeAssistant(string entityName, DateTimeOffset from, DateTimeOffset to);
    Task<IEnumerable<EnvironmentReading>> FetchAllFromHomeAssistant(string namePrefix, DateTimeOffset from, DateTimeOffset to);

    Task<IReadOnlyCollection<EnvironmentReading>> GetReadings();
    Task BulkInsertReadings(IEnumerable<EnvironmentReading> readings);
}
