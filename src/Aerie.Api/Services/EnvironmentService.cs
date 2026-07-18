using System.Collections.Immutable;
using System.Globalization;
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
    StatesClient haStates,
    AerieContext db) : IEnvironmentService
{
    public async Task<IEnumerable<EnvironmentReading>> FetchAllFromHomeAssistant(string namePrefix, DateTimeOffset from, DateTimeOffset to)
    {
        var entities = await GetEntities(namePrefix);

        var result = new List<EnvironmentReading>();
        foreach (var e in entities)
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
            .Select(MapFromHa)
            .Where(a => a is not null)
            .Select(a => a!);

        return mapped;
    }

    public async Task<IEnumerable<EnvironmentReading>> FetchSensorHistoryFromHomeAssistant(string entityId, DateTimeOffset from, DateTimeOffset to, bool isTemperature)
    {
        var history = await haHistory.GetHistory(entityId, from, to);
        if (history == null) return [];

        return history
            .Select(s => MapSensorState(s, isTemperature))
            .OfType<EnvironmentReading>();
    }

    public async IAsyncEnumerable<EnvironmentReading> FetchCurrentFromHomeAssistant(string namePrefix)
    {
        var entities = await GetEntities(namePrefix);

        foreach (var e in entities)
        {
            var state = await haStates.GetState(e);
            var mapped = MapFromHa(state);
            if (mapped is not null)
                yield return mapped;
        }
    }

    private async Task<IEnumerable<string>> GetEntities(string prefix)
    {
        var entities = await haEntity.GetEntities();

        return entities.Where(a => prefix is null
            || a.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase));
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

    private static EnvironmentReading? MapSensorState(StateObject s, bool isTemperature)
    {
        if (!decimal.TryParse(s.State, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return null;

        return new EnvironmentReading
        {
            EntityId = s.EntityId,
            Timestamp = s.LastUpdated,
            Temperature = isTemperature ? value : null,
            Humidity = isTemperature ? null : value,
        };
    }

    private static EnvironmentReading MapClimateState(StateObject s)
    {
        var attributes = MysaAttributes.FromDictionary(s.Attributes);
        return new EnvironmentReading
        {
            DesiredTemperature = attributes.Temperature,
            EntityId = s.EntityId,
            Humidity = attributes.CurrentHumidity,
            IsHeating = attributes.HvacAction == MysaActions.Heating,
            Temperature = attributes.CurrentTemperature,
            Timestamp = s.LastUpdated
        };
    }

    private static EnvironmentReading? MapFromHa(StateObject s)
    {
        if (s.EntityId.StartsWith("sensor."))
        {
            if (s.EntityId.EndsWith("_temperature"))
                return MapSensorState(s, isTemperature: s.EntityId.EndsWith("_temperature"));
            if (s.EntityId.EndsWith("_humidity"))
                return MapSensorState(s, isTemperature: false);
            else
                return null; // nonmapped attribute
        }

        if (s.EntityId.StartsWith("climate."))
        {
            return MapClimateState(s);
        }
        throw new InvalidOperationException($"Unsupported entity ID: [{s.EntityId}]");
    }
}
