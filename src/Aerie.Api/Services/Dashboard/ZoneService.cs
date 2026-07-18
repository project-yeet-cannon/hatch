using System.Globalization;
using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.Dashboard;

public interface IZoneService
{
    Task<IReadOnlyList<ZoneClimate>> GetZonesAsync(DashboardWindow window, CancellationToken ct);
    Task<ZoneClimate?> GetZoneAsync(string entityId, DashboardWindow window, CancellationToken ct);
    Task<IReadOnlyList<TempPoint>> GetReadingsAsync(string entityId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct);
    Task<ComfortRange?> GetComfortAsync(string entityId, CancellationToken ct);
    Task UpsertComfortAsync(string entityId, ComfortRange range, CancellationToken ct);
}

/// <summary>
/// Turns stored EnvironmentReadings + ZoneConfig rows into ZoneClimate DTOs.
/// The ZoneConfig table is the source of truth for name / comfort band /
/// ordering / inclusion; every field falls back sensibly when unconfigured, so
/// a brand-new climate.* entity shows up on the dashboard with no setup.
/// </summary>
public class ZoneService(
    AerieContext db,
    IForecastService forecast,
    IOptions<DashboardOptions> options,
    TimeProvider time) : IZoneService
{
    private DashboardOptions Opt => options.Value;

    public async Task<IReadOnlyList<ZoneClimate>> GetZonesAsync(DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var from = now - window.History;

        var configs = await db.ZoneConfigs.AsNoTracking().ToDictionaryAsync(c => c.EntityId, ct);

        var readingEntityIds = await db.EnvironmentReadings
            .Where(r => r.Timestamp >= from)
            .Select(r => r.EntityId)
            .Distinct()
            .ToListAsync(ct);

        // Zones = anything we've seen readings for, plus configured-and-included
        // entities that haven't reported yet; minus anything explicitly excluded.
        var entityIds = readingEntityIds
            .Union(configs.Values.Where(c => c.Included).Select(c => c.EntityId))
            .Where(id => !configs.TryGetValue(id, out var c) || c.Included)
            .Distinct();

        var zones = new List<(EfZoneConfig? cfg, ZoneClimate zone)>();
        foreach (var id in entityIds)
        {
            configs.TryGetValue(id, out var cfg);
            zones.Add((cfg, await BuildZoneAsync(id, cfg, window, now, ct)));
        }

        return zones
            .OrderBy(z => z.cfg?.SortOrder ?? int.MaxValue)
            .ThenBy(z => z.zone.Name, StringComparer.OrdinalIgnoreCase)
            .Select(z => z.zone)
            .ToList();
    }

    public async Task<ZoneClimate?> GetZoneAsync(string entityId, DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cfg = await db.ZoneConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.EntityId == entityId, ct);
        var from = now - window.History;

        var hasReadings = await db.EnvironmentReadings.AnyAsync(r => r.EntityId == entityId && r.Timestamp >= from, ct);
        if (!hasReadings && cfg is null) return null;

        return await BuildZoneAsync(entityId, cfg, window, now, ct);
    }

    public async Task<IReadOnlyList<TempPoint>> GetReadingsAsync(
        string entityId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct)
    {
        var rows = await LoadReadingsAsync(entityId, from, to, ct);
        return ZoneMath.Bucket(rows.Select(r => (r.Timestamp, r.Temperature)), from, to, bucket);
    }

    public async Task<ComfortRange?> GetComfortAsync(string entityId, CancellationToken ct)
    {
        var cfg = await db.ZoneConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.EntityId == entityId, ct);
        return cfg is { ComfortLowF: { } low, ComfortHighF: { } high } ? new ComfortRange(low, high) : null;
    }

    public async Task UpsertComfortAsync(string entityId, ComfortRange range, CancellationToken ct)
    {
        var cfg = await db.ZoneConfigs.FirstOrDefaultAsync(c => c.EntityId == entityId, ct);
        if (cfg is null)
        {
            cfg = new EfZoneConfig { EntityId = entityId, DisplayName = Humanize(entityId) };
            db.ZoneConfigs.Add(cfg);
        }
        cfg.ComfortLowF = range.LowF;
        cfg.ComfortHighF = range.HighF;
        await db.SaveChangesAsync(ct);
    }

    private async Task<ZoneClimate> BuildZoneAsync(
        string entityId, EfZoneConfig? cfg, DashboardWindow window, DateTimeOffset now, CancellationToken ct)
    {
        var from = now - window.History;
        var rows = await LoadReadingsAsync(entityId, from, now, ct);

        var history = ZoneMath.Bucket(rows.Select(r => (r.Timestamp, r.Temperature)), from, now, window.Bucket);

        var latest = rows.Count > 0 ? rows[^1] : null;
        var currentTempF = latest?.Temperature ?? (history.Count > 0 ? history[^1].TempF : 0m);

        var comfort = ResolveComfort(cfg, latest?.DesiredTemperature);
        var projected = forecast.Project(history, currentTempF, now, window.Forecast, window.Bucket);

        var extremes = ZoneMath.Extremes(history.Concat(projected))
            ?? (new DailyExtreme(Math.Round(currentTempF), now), new DailyExtreme(Math.Round(currentTempF), now));

        var name = cfg?.DisplayName ?? Humanize(entityId);
        return new ZoneClimate(entityId, name, Math.Round(currentTempF, 1), comfort, history, projected, extremes.Item1, extremes.Item2);
    }

    private async Task<List<Reading>> LoadReadingsAsync(string entityId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        await db.EnvironmentReadings
            .Where(r => r.EntityId == entityId && r.Timestamp >= from && r.Timestamp <= to)
            .OrderBy(r => r.Timestamp)
            .Select(r => new Reading(r.Timestamp, r.Temperature, r.DesiredTemperature))
            .ToListAsync(ct);

    private ComfortRange ResolveComfort(EfZoneConfig? cfg, decimal? setpoint)
    {
        if (cfg is { ComfortLowF: { } low, ComfortHighF: { } high })
            return new ComfortRange(low, high);
        if (setpoint is { } sp)
            return new ComfortRange(sp - Opt.ComfortToleranceF, sp + Opt.ComfortToleranceF);
        return new ComfortRange(Opt.DefaultComfortLowF, Opt.DefaultComfortHighF);
    }

    /// <summary>"climate.living_room" -> "Living Room".</summary>
    private static string Humanize(string entityId)
    {
        var local = entityId.Contains('.') ? entityId[(entityId.IndexOf('.') + 1)..] : entityId;
        var words = local.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w)));
    }

    private sealed record Reading(DateTimeOffset Timestamp, decimal? Temperature, decimal? DesiredTemperature);
}
