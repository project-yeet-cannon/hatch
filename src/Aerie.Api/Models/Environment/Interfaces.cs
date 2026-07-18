namespace Aerie.Api.Models.Environment;

public interface IEnvironmentService
{
    Task<IEnumerable<EnvironmentReading>> FetchFromHomeAssistant(string entityName, DateTimeOffset from, DateTimeOffset to);
    Task<IEnumerable<EnvironmentReading>> FetchAllFromHomeAssistant(string namePrefix, DateTimeOffset from, DateTimeOffset to);

    /// <summary>
    /// History for a plain sensor.* entity (a single numeric state, not the
    /// current_temperature/current_humidity attribute pair climate.* entities
    /// expose). The value is stored in Temperature or Humidity depending on
    /// <paramref name="isTemperature"/>.
    /// </summary>
    Task<IEnumerable<EnvironmentReading>> FetchSensorHistoryFromHomeAssistant(string entityId, DateTimeOffset from, DateTimeOffset to, bool isTemperature);

    IAsyncEnumerable<EnvironmentReading> FetchCurrentFromHomeAssistant(string namePrefix);

    Task<IReadOnlyCollection<EnvironmentReading>> GetReadings();
    Task BulkInsertReadings(IEnumerable<EnvironmentReading> readings);
}
