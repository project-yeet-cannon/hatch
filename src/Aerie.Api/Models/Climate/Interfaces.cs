namespace Aerie.Api.Models.Climate;

public interface IClimateService
{
    Task<IEnumerable<ClimateReading>> FetchFromHomeAssistant();

    Task<IReadOnlyCollection<ClimateReading>> GetReadings();
    Task BulkInsertReadings(IEnumerable<ClimateReading> readings);
}
