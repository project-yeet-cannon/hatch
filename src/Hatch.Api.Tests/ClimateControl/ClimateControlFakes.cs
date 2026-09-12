using Hatch.Api.Ef;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Tests.ClimateControl;

/// <summary>Shared doubles for the Phase 1 climate-control tests - see docs/climate-brain-architecture.md.</summary>
internal static class ClimateControlFakes
{
    public static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    /// <summary>A writable channel on a device, both persisted, so the command service can resolve it.</summary>
    public static async Task<EfDeviceChannel> AddChannelAsync(
        AppDbContext db, DeviceChannelMetric metric, string entityId,
        ChannelDirection direction = ChannelDirection.ReadWrite, IReadOnlyList<string>? options = null)
    {
        var device = new EfDevice { Name = $"Device for {entityId}" };
        db.Devices.Add(device);

        var channel = new EfDeviceChannel
        {
            DeviceId = device.Id,
            Device = device,
            Metric = metric,
            HaEntityId = entityId,
            Direction = direction,
            AvailableOptions = ChannelOptionsJson.Serialize(options),
        };
        db.DeviceChannels.Add(channel);
        await db.SaveChangesAsync();
        return channel;
    }
}

internal sealed record HaCall(string Method, string EntityId, string? Argument = null);

/// <summary>Records what reached Home Assistant, and can be told to throw so the Failed path is exercised.</summary>
internal sealed class FakeHomeAssistantCommandService : IHomeAssistantCommandService
{
    public List<HaCall> Calls { get; } = [];
    public Exception? ThrowOnCall { get; set; }

    public Task SetPowerAsync(string entityId, bool on) => Record("SetPower", entityId, on.ToString());
    public Task SetTemperatureAsync(string entityId, decimal temperature) => Record("SetTemperature", entityId, temperature.ToString());
    public Task SetHvacModeAsync(string entityId, string mode) => Record("SetHvacMode", entityId, mode);
    public Task SetFanModeAsync(string entityId, string mode) => Record("SetFanMode", entityId, mode);
    public Task TriggerSceneAsync(string entityId) => Record("TriggerScene", entityId);
    public Task PlayMediaAsync(string entityId, string mediaContentId, string mediaContentType) => Record("PlayMedia", entityId, mediaContentId);

    private Task Record(string method, string entityId, string? argument = null)
    {
        if (ThrowOnCall is { } ex) throw ex;
        Calls.Add(new HaCall(method, entityId, argument));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSiteSettingsService(string? mediaBaseUrl = null, int overrideBackoffMinutes = 120) : ISiteSettingsService
{
    public Task<SiteSettingsSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(new SiteSettingsSnapshot(
        TimeZone: "America/New_York",
        Latitude: 0,
        Longitude: 0,
        WeatherEntity: null,
        ComfortToleranceF: 2m,
        DefaultComfortLowF: 68m,
        DefaultComfortHighF: 72m,
        MediaLibraryBaseUrl: mediaBaseUrl,
        OverrideBackoffMinutes: overrideBackoffMinutes,
        GoogleClientId: null,
        GoogleClientSecret: null,
        GoogleOAuthRedirectUri: null,
        CalendarAgendaDays: 2,
        WeatherAlertProvider: HazardProviders.Nws,
        AirQualityProvider: HazardProviders.OpenMeteo,
        WeatherAlertContact: null,
        AirQualityAlertThresholdAqi: 101,
        HazardMaxSeverityAgeHours: 48,
        AnthropicApiKey: null,
        ImmichBaseUrl: null,
        ImmichApiKey: null,
        ClaudeSubscriptionToken: null,
        LocalPersonName: null));

    /// <summary>Counted rather than ignored: SettingsController is supposed to call it on every write.</summary>
    public int Invalidations { get; private set; }

    public void Invalidate() => Invalidations++;
}
