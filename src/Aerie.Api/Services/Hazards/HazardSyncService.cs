using Aerie.Api.Ef;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aerie.Api.Services.Hazards;

/// <summary>
/// What one hazard sync run did, for the job's log line. The two failure flags
/// are booleans rather than messages because each provider is fail-soft and has
/// already logged its own reason - this is only how the job says which half of
/// the panel is stale.
/// </summary>
public record HazardSyncResult(
    int AlertsWritten,
    int AlertsDeactivated,
    int SamplesWritten,
    bool WeatherFailed,
    bool AirQualityFailed);

public interface IHazardSyncService
{
    /// <summary>Fetches both halves and caches them. Never throws: one broken provider leaves the other's data fresh.</summary>
    Task<HazardSyncResult> SyncAsync(CancellationToken ct);
}

/// <summary>
/// The fetch half of the hazard fetch/cache/read split (docs/plans/kiosk.md
/// track B): this talks to the providers and writes Postgres, and
/// GET /api/dashboard reads Postgres and never talks to a provider.
///
/// Alerts are upserted and then *deactivated* rather than deleted when the
/// provider stops returning them - "what was the house warned about last
/// night" is worth keeping, and the rows are tiny. Air quality is insert-only:
/// the unique index on (Source, Timestamp) makes re-fetching an hour already
/// stored a no-op, which is most of what a 15-minute job fetching hourly data
/// does.
/// </summary>
public class HazardSyncService(
    AerieContext db,
    IHazardProviderResolver providers,
    ISiteSettingsService siteSettings,
    TimeProvider time,
    ILogger<HazardSyncService> logger) : IHazardSyncService
{
    /// <summary>Bounds retries on the race where a second replica inserts a sample between our existing-rows check and SaveChanges. Same shape, and the same reason, as ChannelHistoryWriter.</summary>
    private const int MaxAttempts = 3;

    public async Task<HazardSyncResult> SyncAsync(CancellationToken ct)
    {
        var settings = await siteSettings.GetAsync(ct);
        var now = time.GetUtcNow();

        var (written, deactivated, weatherFailed) = await SyncWeatherAlertsAsync(settings, now, ct);
        var (samples, airQualityFailed) = await SyncAirQualityAsync(settings, now, ct);

        return new HazardSyncResult(written, deactivated, samples, weatherFailed, airQualityFailed);
    }

    /// <summary>
    /// Each provider gets its own try/catch even though both are fail-soft by
    /// contract: a bug in one provider's parsing must not cost the other half
    /// its sync, and the DB writes below can fail on their own terms.
    /// </summary>
    private async Task<(int Written, int Deactivated, bool Failed)> SyncWeatherAlertsAsync(
        SiteSettingsSnapshot settings, DateTimeOffset now, CancellationToken ct)
    {
        var provider = providers.ResolveWeatherAlerts(settings.WeatherAlertProvider);
        if (provider is null) return (0, 0, false);

        try
        {
            var fetched = await provider.GetActiveAlertsAsync(settings.Latitude, settings.Longitude, ct);

            // Two alerts sharing an id in one response would violate the
            // unique index inside a single SaveChanges; the last one wins,
            // which is the same thing the upsert below would do sequentially.
            var byId = fetched
                .GroupBy(a => a.ProviderAlertId)
                .ToDictionary(g => g.Key, g => g.Last());

            // Everything currently active plus everything just fetched: the
            // first set is what may need deactivating, the second is what may
            // need updating, and an alert that lapsed and returned is in both.
            var fetchedIds = byId.Keys.ToArray();
            var stored = await db.WeatherAlerts
                .Where(a => a.Source == provider.Name)
                .Where(a => a.Active || fetchedIds.Contains(a.ProviderAlertId))
                .ToListAsync(ct);

            var written = 0;
            var deactivated = 0;

            foreach (var row in stored)
            {
                if (byId.Remove(row.ProviderAlertId, out var record))
                {
                    Apply(record, row, now);
                    written++;
                }
                else if (row.Active)
                {
                    // The provider no longer lists it, which is how an alert
                    // ends: NWS drops a cancelled or expired one from the feed
                    // rather than announcing its end.
                    row.Active = false;
                    deactivated++;
                }
            }

            foreach (var record in byId.Values)
            {
                var row = new EfWeatherAlert
                {
                    Source = provider.Name,
                    ProviderAlertId = record.ProviderAlertId,
                    Event = record.Event,
                    FetchedAt = now,
                };
                Apply(record, row, now);
                db.WeatherAlerts.Add(row);
                written++;
            }

            await db.SaveChangesAsync(ct);
            return (written, deactivated, false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Weather alert sync from {Provider} failed; the cached alerts stay as they are", provider.Name);
            db.ChangeTracker.Clear();
            return (0, 0, true);
        }
    }

    private async Task<(int Samples, bool Failed)> SyncAirQualityAsync(
        SiteSettingsSnapshot settings, DateTimeOffset now, CancellationToken ct)
    {
        var provider = providers.ResolveAirQuality(settings.AirQualityProvider);
        if (provider is null) return (0, false);

        try
        {
            var reading = await provider.GetCurrentAsync(settings.Latitude, settings.Longitude, ct);
            if (reading is null) return (0, false);

            var candidates = Hourly(reading);
            var timestamps = candidates.Select(c => c.Timestamp).ToArray();

            var existing = await db.AirQualitySamples.AsNoTracking()
                .Where(s => s.Source == provider.Name && timestamps.Contains(s.Timestamp))
                .Select(s => s.Timestamp)
                .ToListAsync(ct);
            var stored = existing.ToHashSet();

            var inserted = await InsertSamplesAsync(
                provider.Name,
                [.. candidates.Where(c => !stored.Contains(c.Timestamp))],
                now,
                ct);

            return (inserted, false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Air quality sync from {Provider} failed; the cached samples stay as they are", provider.Name);
            db.ChangeTracker.Clear();
            return (0, true);
        }
    }

    /// <summary>
    /// The hourly series with the current reading folded onto its own hour.
    ///
    /// Two things make this worth doing rather than storing both: the current
    /// block is the only one carrying component pollutants, so the hour it
    /// belongs to should keep them; and its timestamp is a quarter-hour
    /// observation rather than an hour, which stored as-is would turn a table
    /// of 24 rows a day into 96 that no longer line up with anything.
    /// </summary>
    private static IReadOnlyList<AirQualitySampleRecord> Hourly(AirQualityReading reading)
    {
        var byHour = reading.Hourly
            .GroupBy(h => Hour(h.Timestamp))
            .ToDictionary(g => g.Key, g => g.Last() with { Timestamp = g.Key });

        byHour[Hour(reading.Current.Timestamp)] = reading.Current with { Timestamp = Hour(reading.Current.Timestamp) };

        return [.. byHour.Values.OrderBy(s => s.Timestamp)];
    }

    private static DateTimeOffset Hour(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, 0, 0, at.Offset);

    /// <summary>
    /// Inserts the samples that were missing as of the check above. The
    /// pre-filter normally means nothing collides here; on the race where a
    /// second replica's firing beat us to an hour, the rows that now exist are
    /// dropped and the remainder retried rather than the batch failing over
    /// one duplicate - the handling ChannelHistoryWriter established for
    /// exactly this shape of unique key.
    /// </summary>
    private async Task<int> InsertSamplesAsync(
        string source, IReadOnlyList<AirQualitySampleRecord> samples, DateTimeOffset now, CancellationToken ct)
    {
        var pending = samples;

        for (var attempt = 1; pending.Count > 0; attempt++)
        {
            db.AirQualitySamples.AddRange(pending.Select(s => new EfAirQualitySample
            {
                Source = source,
                Timestamp = s.Timestamp,
                UsAqi = s.UsAqi,
                Pm25 = s.Pm25,
                Pm10 = s.Pm10,
                Ozone = s.Ozone,
                No2 = s.No2,
                FetchedAt = now,
            }));

            try
            {
                await db.SaveChangesAsync(ct);
                return pending.Count;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && IsUniqueViolation(ex))
            {
                logger.LogInformation(
                    "Air quality sample(s) from {Source} hit a duplicate-hour race (attempt {Attempt}), re-filtering and retrying",
                    source, attempt);
                db.ChangeTracker.Clear();

                var timestamps = pending.Select(p => p.Timestamp).ToArray();
                var stored = (await db.AirQualitySamples.AsNoTracking()
                    .Where(s => s.Source == source && timestamps.Contains(s.Timestamp))
                    .Select(s => s.Timestamp)
                    .ToListAsync(ct)).ToHashSet();

                pending = [.. pending.Where(p => !stored.Contains(p.Timestamp))];
            }
        }

        return 0;
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Copies a fetched alert onto its row. Everything except the natural key
    /// is overwritten every cycle, because NWS re-issues a live alert as an
    /// Update with a changed end time or narrative and the row has to follow -
    /// and Active is set here so an alert that lapsed and came back returns to
    /// the wall rather than staying deactivated.
    /// </summary>
    private static void Apply(WeatherAlertRecord record, EfWeatherAlert row, DateTimeOffset now)
    {
        row.Event = record.Event;
        row.Headline = record.Headline;
        row.Description = record.Description;
        row.Instruction = record.Instruction;
        row.Severity = record.Severity;
        row.Onset = record.Onset;
        row.Ends = record.Ends;
        row.AreaDescription = record.AreaDescription;
        row.Active = true;
        row.FetchedAt = now;
    }
}
