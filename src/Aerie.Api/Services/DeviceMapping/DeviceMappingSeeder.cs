using System.Globalization;
using Aerie.Api.Common;
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
public class DeviceMappingSeeder(
    AerieContext db, IOptions<DashboardOptions> options, ISecrets secrets, IHomeAssistantStateReader stateReader
) : IDeviceMappingSeeder
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedZonesAndDevicesAsync(ct);
        await SeedSiteSettingsAsync(ct);
        await SeedHomeAssistantConnectionAsync(ct);
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

            // No sibling entities to check here (unlike Discovery, which groups by HA
            // device) - this is a flat list of already-known climate.* entity ids, so
            // temperature/humidity always come from the climate entity's own attributes.
            var climateState = await stateReader.TryGetStateAsync(entityId, ct);
            var channels = ThermostatChannelBuilder.Build(entityId, [], climateState);
            db.DeviceChannels.AddRange(channels.Select(c => new EfDeviceChannel
            {
                Device = device,
                Metric = c.Metric,
                HaEntityId = c.HaEntityId,
                HaAttribute = c.HaAttribute,
                Direction = c.Direction,
                AvailableOptions = ChannelOptionsJson.Serialize(c.AvailableOptions),
            }));
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedSiteSettingsAsync(CancellationToken ct)
    {
        if (await db.SiteSettings.AnyAsync(ct)) return;

        var opt = options.Value;
        db.SiteSettings.AddRange(
            new EfSiteSetting { Key = SiteSettingKeys.TimeZone, Value = opt.TimeZone },
            new EfSiteSetting { Key = SiteSettingKeys.Latitude, Value = opt.Latitude.ToString(CultureInfo.InvariantCulture) },
            new EfSiteSetting { Key = SiteSettingKeys.Longitude, Value = opt.Longitude.ToString(CultureInfo.InvariantCulture) },
            new EfSiteSetting { Key = SiteSettingKeys.WeatherEntity, Value = opt.WeatherEntity ?? "" },
            new EfSiteSetting { Key = SiteSettingKeys.ComfortToleranceF, Value = opt.ComfortToleranceF.ToString(CultureInfo.InvariantCulture) },
            new EfSiteSetting { Key = SiteSettingKeys.DefaultComfortLowF, Value = opt.DefaultComfortLowF.ToString(CultureInfo.InvariantCulture) },
            new EfSiteSetting { Key = SiteSettingKeys.DefaultComfortHighF, Value = opt.DefaultComfortHighF.ToString(CultureInfo.InvariantCulture) });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One-time import of ha_host/ha_port/ha_token from .env.json (the old
    /// startup-config source, see EnvSecrets) into SiteSettings, now that the
    /// admin Settings page owns them. Guarded per-key rather than on the
    /// whole SiteSettings table being empty, since that table is almost
    /// always already populated by SeedSiteSettingsAsync by the time this
    /// runs. No-ops (leaving the fields for the admin UI) if .env.json is
    /// missing or doesn't have all three.
    /// </summary>
    private async Task SeedHomeAssistantConnectionAsync(CancellationToken ct)
    {
        if (await db.SiteSettings.AnyAsync(s => s.Key == SiteSettingKeys.HomeAssistantHost, ct)) return;

        var host = secrets.GetSecret("ha_host");
        var port = secrets.GetSecret("ha_port");
        var token = secrets.GetSecret("ha_token");
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(token)) return;

        db.SiteSettings.AddRange(
            new EfSiteSetting { Key = SiteSettingKeys.HomeAssistantHost, Value = host },
            new EfSiteSetting { Key = SiteSettingKeys.HomeAssistantPort, Value = port },
            new EfSiteSetting { Key = SiteSettingKeys.HomeAssistantToken, Value = SecretObfuscator.Obfuscate(token) });

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
