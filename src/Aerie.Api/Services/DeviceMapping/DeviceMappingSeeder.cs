using System.Globalization;
using Aerie.Api.Ef;
using Aerie.Api.Services.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.DeviceMapping;

public interface IDeviceMappingSeeder
{
    Task SeedAsync(CancellationToken ct = default);
}

/// <summary>
/// One-time backfill from the pre-Device-mapping world (EfZoneConfig,
/// EnvironmentReadings, the "Dashboard" config section) into
/// Zone/Device/DeviceChannel/SiteSetting. Phase 1 of
/// docs/device-architecture.md. The zone/device half and the site-settings
/// half are each idempotent on their own table being empty, so this is safe
/// to call on every startup - see Program.cs.
///
/// Deliberately does NOT touch sensor.* (hygrometer/outside) entities: the
/// architecture doc scopes Phase 1's auto-creation to climate.* only,
/// leaving hygrometer grouping to the Discovery service (Phase 2) and the
/// admin import flow (Phase 3). OutsideTemperatureEntity/OutsideHumidityEntity
/// stay in Dashboard config, unmigrated, until an admin imports them as a
/// Zone(Kind=Outside) + Device.
/// </summary>
public class DeviceMappingSeeder(AerieContext db, IOptions<DashboardOptions> options) : IDeviceMappingSeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedZonesAndDevicesAsync(ct);
        await SeedSiteSettingsAsync(ct);
    }

    private async Task SeedZonesAndDevicesAsync(CancellationToken ct)
    {
        // Guards on Devices rather than Zones: the AddOutsideZone migration
        // inserts a Zone(Kind=Outside) row with no paired Device ahead of
        // this running, so a Zones-based check would find that row and skip
        // the EfZoneConfig/climate-entity backfill below on every fresh DB.
        if (await db.Devices.AnyAsync(ct)) return;

        var configs = await db.ZoneConfigs.AsNoTracking().ToDictionaryAsync(c => c.EntityId, ct);

        var climateEntityIds = await db.EnvironmentReadings
            .Where(r => r.EntityId.StartsWith("climate."))
            .Select(r => r.EntityId)
            .Distinct()
            .ToListAsync(ct);

        var allEntityIds = configs.Keys.Union(climateEntityIds).OrderBy(id => id, StringComparer.Ordinal);

        // Entities with no ZoneConfig row (never manually configured) sort after
        // the configured ones, in the order ZoneService would've fallen back to.
        var nextSortOrder = configs.Values.Count == 0 ? 0 : configs.Values.Max(c => c.SortOrder) + 1;

        foreach (var entityId in allEntityIds)
        {
            configs.TryGetValue(entityId, out var cfg);

            var zone = new EfZone
            {
                Name = cfg?.DisplayName ?? Humanize(entityId),
                Kind = ZoneKind.Interior,
                ComfortLowF = cfg?.ComfortLowF,
                ComfortHighF = cfg?.ComfortHighF,
                SortOrder = cfg?.SortOrder ?? nextSortOrder++,
                Included = cfg?.Included ?? true,
            };
            db.Zones.Add(zone);

            var device = new EfDevice
            {
                Name = zone.Name,
                Kind = DeviceKind.Thermostat,
                Zone = zone,
                Enabled = true,
            };
            db.Devices.Add(device);

            db.DeviceChannels.AddRange(
                new EfDeviceChannel { Device = device, Metric = DeviceChannelMetric.Temperature, HaEntityId = entityId, HaAttribute = "current_temperature", Direction = ChannelDirection.Read },
                new EfDeviceChannel { Device = device, Metric = DeviceChannelMetric.Humidity, HaEntityId = entityId, HaAttribute = "current_humidity", Direction = ChannelDirection.Read },
                new EfDeviceChannel { Device = device, Metric = DeviceChannelMetric.SetpointTemperature, HaEntityId = entityId, HaAttribute = "temperature", Direction = ChannelDirection.ReadWrite },
                new EfDeviceChannel { Device = device, Metric = DeviceChannelMetric.HvacAction, HaEntityId = entityId, HaAttribute = "hvac_action", Direction = ChannelDirection.Read });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSiteSettingsAsync(CancellationToken ct)
    {
        if (await db.SiteSettings.AnyAsync(ct)) return;

        var opt = options.Value;
        db.SiteSettings.AddRange(
            new EfSiteSetting { Key = SiteSettingKeys.TimeZone, Value = opt.TimeZone },
            new EfSiteSetting { Key = SiteSettingKeys.SunEntity, Value = opt.SunEntity },
            new EfSiteSetting { Key = SiteSettingKeys.WeatherEntity, Value = opt.WeatherEntity ?? "" },
            new EfSiteSetting { Key = SiteSettingKeys.ComfortToleranceF, Value = opt.ComfortToleranceF.ToString(CultureInfo.InvariantCulture) },
            new EfSiteSetting { Key = SiteSettingKeys.DefaultComfortLowF, Value = opt.DefaultComfortLowF.ToString(CultureInfo.InvariantCulture) },
            new EfSiteSetting { Key = SiteSettingKeys.DefaultComfortHighF, Value = opt.DefaultComfortHighF.ToString(CultureInfo.InvariantCulture) });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>"climate.living_room" -> "Living Room". Mirrors ZoneService.Humanize so fallback zone names match what the dashboard shows today.</summary>
    private static string Humanize(string entityId)
    {
        var local = entityId.Contains('.') ? entityId[(entityId.IndexOf('.') + 1)..] : entityId;
        var words = local.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(w)));
    }
}
