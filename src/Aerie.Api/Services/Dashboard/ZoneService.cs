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
/// GetZonesAsync loads temperature and setpoint measurements for every zone in
/// two queries total, rather than two round trips per zone: with N zones on
/// the dashboard, a per-zone loop turns into a full extra network round trip
/// per zone for no benefit, since Postgres can filter/group all of them at
/// once just as cheaply as one.
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
        var from = now - window.History;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var settings = await siteSettings.GetAsync(ct);

        var zoneRows = await db.Zones.AsNoTracking()
            .Where(z => z.Kind == ZoneKind.Interior && z.Included)
            .OrderBy(z => z.SortOrder)
            .ThenBy(z => z.Name)
            .ToListAsync(ct);

        var zoneIds = zoneRows.Select(z => z.Id).ToList();
        var tempsByZone = await LoadMeasurementsByZoneAsync(db, zoneIds, DeviceChannelMetric.Temperature, from, now, ct);
        var setpointsByZone = await LoadLatestByZoneAsync(db, zoneIds, DeviceChannelMetric.SetpointTemperature, from, now, ct);

        return zoneRows
            .Select(zone => BuildZoneClimate(
                zone, settings, window, now,
                tempsByZone.GetValueOrDefault(zone.Id) ?? [],
                setpointsByZone.GetValueOrDefault(zone.Id)))
            .ToList();
    }

    public async Task<ZoneClimate?> GetZoneAsync(Guid zoneId, DashboardWindow window, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var from = now - window.History;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var zone = await db.Zones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId, ct);
        if (zone is null) return null;

        var settings = await siteSettings.GetAsync(ct);
        var rows = await LoadMeasurementsAsync(db, zoneId, DeviceChannelMetric.Temperature, from, now, ct);
        var setpoint = await ZoneMeasurements.LatestAsync(db, zoneId, DeviceChannelMetric.SetpointTemperature, from, now, ct);
        return BuildZoneClimate(zone, settings, window, now, rows, setpoint);
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

    private ZoneClimate BuildZoneClimate(
        EfZone zone, SiteSettingsSnapshot settings, DashboardWindow window, DateTimeOffset now,
        IReadOnlyList<Sample> rows, decimal? setpoint)
    {
        var from = now - window.History;
        var history = ZoneMath.Bucket(rows.Select(r => (r.Timestamp, (decimal?)r.Value)), from, now, window.Bucket);
        var currentTempF = rows.Count > 0 ? rows[^1].Value : (history.Count > 0 ? history[^1].TempF : (decimal?)null);

        var comfort = ResolveComfort(zone, setpoint, settings);
        var projected = currentTempF is { } temp
            ? forecast.Project(history, temp, now, window.Forecast, window.Bucket)
            : [];
        var extremes = ZoneMath.Extremes(history.Concat(projected));
        DailyExtreme? low = extremes?.Low;
        DailyExtreme? high = extremes?.High;
        if (extremes is null && currentTempF is { } fallback)
        {
            low = new DailyExtreme(Math.Round(fallback), now);
            high = new DailyExtreme(Math.Round(fallback), now);
        }

        return new ZoneClimate(
            zone.Id.ToString(), zone.Name, currentTempF is { } t ? Math.Round(t, 1) : null,
            comfort, history, projected, low, high);
    }

    private static Task<List<Sample>> LoadMeasurementsAsync(
        AerieContext db, Guid zoneId, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        ZoneMeasurements.ForZone(db, zoneId, metric, from, to)
            .OrderBy(m => m.Timestamp)
            .Select(m => new Sample(m.Timestamp, m.Value))
            .ToListAsync(ct);

    /// <summary>All matching measurements for the given zones in two queries total, grouped by zone.</summary>
    private static async Task<Dictionary<Guid, List<Sample>>> LoadMeasurementsByZoneAsync(
        AerieContext db, IReadOnlyList<Guid> zoneIds, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (zoneIds.Count == 0) return [];

        var channelZones = await ZoneMeasurements.ChannelZonesAsync(db, zoneIds, metric, ct);
        if (channelZones.Count == 0) return [];

        var channelIds = channelZones.Keys.ToList();
        var rows = await db.Measurements.AsNoTracking()
            .Where(m => channelIds.Contains(m.ChannelId) && m.Timestamp >= from && m.Timestamp <= to)
            .OrderBy(m => m.Timestamp)
            .Select(m => new { m.ChannelId, m.Timestamp, m.Value })
            .ToListAsync(ct);

        return rows.GroupBy(r => channelZones[r.ChannelId])
            .ToDictionary(g => g.Key, g => g.Select(r => new Sample(r.Timestamp, r.Value)).ToList());
    }

    /// <summary>Latest matching measurement per zone in two queries total.</summary>
    private static async Task<Dictionary<Guid, decimal?>> LoadLatestByZoneAsync(
        AerieContext db, IReadOnlyList<Guid> zoneIds, DeviceChannelMetric metric, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        if (zoneIds.Count == 0) return [];

        var channelZones = await ZoneMeasurements.ChannelZonesAsync(db, zoneIds, metric, ct);
        if (channelZones.Count == 0) return [];

        var channelIds = channelZones.Keys.ToList();
        var rows = await db.Measurements.AsNoTracking()
            .Where(m => channelIds.Contains(m.ChannelId) && m.Timestamp >= from && m.Timestamp <= to)
            .OrderByDescending(m => m.Timestamp)
            .Select(m => new { m.ChannelId, m.Value })
            .ToListAsync(ct);

        return rows.GroupBy(r => channelZones[r.ChannelId])
            .ToDictionary(g => g.Key, g => (decimal?)g.First().Value);
    }

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
