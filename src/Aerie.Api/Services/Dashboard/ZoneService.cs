using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Dashboard;

public interface IZoneService
{
    Task<IReadOnlyList<ZoneClimate>> GetZonesAsync(DashboardWindow window, CancellationToken ct);
    Task<ZoneClimate?> GetZoneAsync(Guid zoneId, DashboardWindow window, CancellationToken ct);
    Task<IReadOnlyList<TempPoint>> GetReadingsAsync(Guid zoneId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct);
    Task<ComfortRange?> GetComfortAsync(Guid zoneId, CancellationToken ct);
    Task<bool> UpsertComfortAsync(Guid zoneId, ComfortRange range, CancellationToken ct);
}

/// <summary>
/// Turns Zone -> Device -> DeviceChannel -> Measurement rows into ZoneClimate
/// DTOs (docs/device-architecture.md Phase 5). Zone is the source of truth for
/// name / comfort band / ordering / inclusion; a zone's "current temperature"
/// and history are the most recent values across all of its enabled devices'
/// Temperature channels, so a zone with more than one temperature-reporting
/// device still resolves to one series instead of requiring the caller to pick
/// one - today's seeding keeps that 1:1, but the domain model allows more.
/// </summary>
public class ZoneService(
    IDbContextFactory<AerieContext> dbFactory,
    IForecastService forecast,
    ISiteSettingsService siteSettings,
    TimeProvider time) : IZoneService
{
    public async Task<IReadOnlyList<ZoneClimate>> GetZonesAsync(DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await siteSettings.GetAsync(ct);

        var zoneRows = await db.Zones.AsNoTracking()
            .Where(z => z.Kind == ZoneKind.Interior && z.Included)
            .OrderBy(z => z.SortOrder)
            .ThenBy(z => z.Name)
            .ToListAsync(ct);

        var result = new List<ZoneClimate>(zoneRows.Count);
        foreach (var zone in zoneRows)
            result.Add(await BuildZoneAsync(db, zone, settings, window, now, ct));

        return result;
    }

    public async Task<ZoneClimate?> GetZoneAsync(Guid zoneId, DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId, ct);
        if (zone is null) return null;

        var settings = await siteSettings.GetAsync(ct);
        return await BuildZoneAsync(db, zone, settings, window, now, ct);
    }

    public async Task<IReadOnlyList<TempPoint>> GetReadingsAsync(
        Guid zoneId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await LoadMeasurementsAsync(db, zoneId, DeviceChannelMetric.Temperature, from, to, ct);
        return ZoneMath.Bucket(rows.Select(r => (r.Timestamp, (decimal?)r.Value)), from, to, bucket);
    }

    public async Task<ComfortRange?> GetComfortAsync(Guid zoneId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId, ct);
        return zone is { ComfortLowF: { } low, ComfortHighF: { } high } ? new ComfortRange(low, high) : null;
    }

    public async Task<bool> UpsertComfortAsync(Guid zoneId, ComfortRange range, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Zones.FirstOrDefaultAsync(z => z.Id == zoneId, ct);
        if (zone is null) return false;

        zone.ComfortLowF = range.LowF;
        zone.ComfortHighF = range.HighF;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<ZoneClimate> BuildZoneAsync(
        AerieContext db, EfZone zone, SiteSettingsSnapshot settings, DashboardWindow window, DateTimeOffset now, CancellationToken ct)
    {
        var from = now - window.History;
        var rows = await LoadMeasurementsAsync(db, zone.Id, DeviceChannelMetric.Temperature, from, now, ct);

        var history = ZoneMath.Bucket(rows.Select(r => (r.Timestamp, (decimal?)r.Value)), from, now, window.Bucket);
        var currentTempF = rows.Count > 0 ? rows[^1].Value : (history.Count > 0 ? history[^1].TempF : 0m);

        var setpoint = await ZoneMeasurements.LatestAsync(db, zone.Id, DeviceChannelMetric.SetpointTemperature, from, now, ct);
        var comfort = ResolveComfort(zone, setpoint, settings);

        var projected = forecast.Project(history, currentTempF, now, window.Forecast, window.Bucket);
        var extremes = ZoneMath.Extremes(history.Concat(projected))
            ?? (new DailyExtreme(Math.Round(currentTempF), now), new DailyExtreme(Math.Round(currentTempF), now));

        return new ZoneClimate(zone.Id.ToString(), zone.Name, Math.Round(currentTempF, 1), comfort, history, projected, extremes.Item1, extremes.Item2);
    }

    private static Task<List<Sample>> LoadMeasurementsAsync(
        AerieContext db, Guid zoneId, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ZoneMeasurements.ForZone(db, zoneId, metric, from, to)
            .OrderBy(m => m.Timestamp)
            .Select(m => new Sample(m.Timestamp, m.Value))
            .ToListAsync(ct);

    private static ComfortRange ResolveComfort(EfZone zone, decimal? setpoint, SiteSettingsSnapshot settings)
    {
        if (zone is { ComfortLowF: { } low, ComfortHighF: { } high })
            return new ComfortRange(low, high);
        if (setpoint is { } sp)
            return new ComfortRange(sp - settings.ComfortToleranceF, sp + settings.ComfortToleranceF);
        return new ComfortRange(settings.DefaultComfortLowF, settings.DefaultComfortHighF);
    }

    private sealed record Sample(DateTimeOffset Timestamp, decimal Value);
}
