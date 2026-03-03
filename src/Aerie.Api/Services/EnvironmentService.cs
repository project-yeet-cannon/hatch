using System.Collections.Immutable;
using Aerie.Api.Ef;
using Aerie.Api.Models.Environment;
using Aerie.Api.Models.HomeAssistant;
using HADotNet.Core.Clients;
using HADotNet.Core.Models;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Npgsql;

namespace Aerie.Api.Services;

// Most network calls are in serial so we are
// kind to the tiny HA server
public class EnvironmentService(
    EntityClient haEntity,
    HistoryClient haHistory,
    AerieContext db) : IEnvironmentService
{
    public async Task<IEnumerable<EnvironmentReading>> FetchAllFromHomeAssistant(string namePrefix, DateTimeOffset from, DateTimeOffset to)
    {
        var entities = await haEntity.GetEntities();

        var filtered = entities.Where(a => namePrefix is null
            || a.StartsWith(namePrefix, StringComparison.InvariantCultureIgnoreCase));

        var result = new List<EnvironmentReading>();
        foreach (var e in filtered)
        {
            var readings = await FetchFromHomeAssistant(e, from, to);
            result.AddRange(readings);
        }
        return result;
    }

    public async Task<IEnumerable<EnvironmentReading>> FetchFromHomeAssistant(string entityName, DateTimeOffset from, DateTimeOffset to)
    {
        var history = await haHistory.GetHistory(entityName, from, to);
        if (history == null) return [];

        var mapped = history
            .Select(MapFromHa);

        return mapped;
    }

    public async Task BulkInsertReadings(IEnumerable<EnvironmentReading> readings)
    {
        // slow but EF doesn't have a nice BulkInsert which ignores duplicates
        foreach (var r in readings.Select(MapToEfReading))
        {
            Console.WriteLine($"Adding {JsonConvert.SerializeObject(r)}");
            try
            {
                await db.Set<EfEnvironmentReading>().AddAsync(r);
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException dx)
            {
                var ex = dx.InnerException as PostgresException;
                if (ex is not null
                    && ex.SqlState == PostgresErrorCodes.UniqueViolation
                    && ex.ConstraintName == "IX_EnvironmentReadings_EntityId_Timestamp")
                    continue;
                throw;
            }
        }
    }

    public async Task<IReadOnlyCollection<EnvironmentReading>> GetReadings()
    {
        var readings = await db.Set<EfEnvironmentReading>().ToListAsync();
        return readings.Select(MapFromEfReading).ToImmutableList();
    }

    private EfEnvironmentReading MapToEfReading(EnvironmentReading r)
        => new EfEnvironmentReading
        {
            Id = r.Id,
            EntityId = r.EntityId,
            Timestamp = r.Timestamp,
            Temperature = r.Temperature,
            Humidity = r.Humidity,
            DesiredTemperature = r.DesiredTemperature,
            IsHeating = r.IsHeating
        };

    private EnvironmentReading MapFromEfReading(EfEnvironmentReading r)
        => new EnvironmentReading
        {
            Id = r.Id,
            EntityId = r.EntityId,
            Timestamp = r.Timestamp,
            Temperature = r.Temperature,
            Humidity = r.Humidity,
            DesiredTemperature = r.DesiredTemperature,
            IsHeating = r.IsHeating
        };

    private static EnvironmentReading MapFromHa(StateObject s)
    {
        var attributes = MysaAttributes.FromDictionary(s.Attributes);

        return new EnvironmentReading
        {
            DesiredTemperature = attributes.Temperature,
            EntityId = s.EntityId,
            Humidity = attributes.CurrentHumidity,
            IsHeating = attributes.HvacAction == MysaActions.Heating,
            Temperature = attributes.Temperature,
            Timestamp = s.LastUpdated
        };
    }
}
