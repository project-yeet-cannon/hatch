using Aerie.Api.Ef;
using Aerie.Api.Jobs;
using Aerie.Api.Models.DeviceMapping;
using Aerie.Api.Services.Dashboard;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Aerie.Api.Controllers;

/// <summary>CRUD for Devices and their Channels, including assigning a Device to a Zone via Update (docs/device-architecture.md Phase 2).</summary>
[ApiController]
[Route("api/[controller]")]
public class DevicesController(
    AerieContext db, IScheduler scheduler, IHomeAssistantCommandService command, IHomeAssistantStateReader stateReader
) : ControllerBase
{
    /// <summary>Allowance for clock skew between this server and the client when rejecting "to" timestamps in the future.</summary>
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(20);

    [HttpGet]
    public async Task<IReadOnlyList<DeviceDto>> GetAll(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Include(d => d.Channels).ToListAsync(ct);
        var latest = await ChannelLatestValues.GetLatestAsync(db, devices.SelectMany(d => d.Channels).Select(c => c.Id).ToList(), ct);
        return devices.Select(d => ToDto(d, latest)).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeviceDto>> Get(Guid id, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();
        var latest = await ChannelLatestValues.GetLatestAsync(db, device.Channels.Select(c => c.Id).ToList(), ct);
        return ToDto(device, latest);
    }

    [HttpPost]
    public async Task<ActionResult<DeviceDto>> Create(DeviceWriteRequest request, CancellationToken ct)
    {
        var device = new EfDevice
        {
            Name = request.Name,
            Kind = request.Kind,
            ZoneId = request.ZoneId,
            HaDeviceId = request.HaDeviceId,
            Enabled = request.Enabled,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = device.Id }, ToDto(device, new Dictionary<Guid, ChannelLatestValue>()));
    }

    /// <summary>Updates device scalars, including ZoneId - this is how a device is assigned to (or removed from) a Zone.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DeviceDto>> Update(Guid id, DeviceWriteRequest request, CancellationToken ct)
    {
        var device = await db.Devices.Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return NotFound();

        device.Name = request.Name;
        device.Kind = request.Kind;
        device.ZoneId = request.ZoneId;
        device.HaDeviceId = request.HaDeviceId;
        device.Enabled = request.Enabled;
        await db.SaveChangesAsync(ct);
        var latest = await ChannelLatestValues.GetLatestAsync(db, device.Channels.Select(c => c.Id).ToList(), ct);
        return ToDto(device, latest);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound();
        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/channels")]
    public async Task<ActionResult<DeviceChannelDto>> AddChannel(Guid id, DeviceChannelWriteRequest request, CancellationToken ct)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == id, ct)) return NotFound();

        var channel = new EfDeviceChannel
        {
            DeviceId = id,
            Metric = request.Metric,
            HaEntityId = request.HaEntityId,
            HaAttribute = request.HaAttribute,
            Direction = request.Direction,
            AvailableOptions = ChannelOptionsJson.Serialize(request.AvailableOptions),
        };
        db.DeviceChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return ToDto(channel, default);
    }

    [HttpPut("{id:guid}/channels/{channelId:guid}")]
    public async Task<ActionResult<DeviceChannelDto>> UpdateChannel(Guid id, Guid channelId, DeviceChannelWriteRequest request, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();

        channel.Metric = request.Metric;
        channel.HaEntityId = request.HaEntityId;
        channel.HaAttribute = request.HaAttribute;
        channel.Direction = request.Direction;
        channel.AvailableOptions = ChannelOptionsJson.Serialize(request.AvailableOptions);
        await db.SaveChangesAsync(ct);
        var latest = await ChannelLatestValues.GetLatestAsync(db, [channel.Id], ct);
        return ToDto(channel, latest.GetValueOrDefault(channel.Id));
    }

    [HttpDelete("{id:guid}/channels/{channelId:guid}")]
    public async Task<IActionResult> DeleteChannel(Guid id, Guid channelId, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();
        db.DeviceChannels.Remove(channel);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Turns a PowerState channel's underlying HA switch on/off. The channel's own Measurement/StateChange row updates on SampleChannels' next poll rather than here, matching every other ReadWrite channel's read latency.</summary>
    [HttpPost("{id:guid}/channels/{channelId:guid}/power")]
    public async Task<IActionResult> SetPower(Guid id, Guid channelId, ChannelPowerRequest request, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();
        if (channel.Metric != DeviceChannelMetric.PowerState || channel.Direction != ChannelDirection.ReadWrite)
            return BadRequest("Channel is not a writable PowerState channel");

        await command.SetPowerAsync(channel.HaEntityId, request.On);
        return Accepted();
    }

    /// <summary>Writes a SetpointTemperature channel's underlying HA climate entity setpoint. Same read-latency note as SetPower - the channel's own Measurement row updates on SampleChannels' next poll.</summary>
    [HttpPost("{id:guid}/channels/{channelId:guid}/setpoint")]
    public async Task<IActionResult> SetSetpoint(Guid id, Guid channelId, ChannelSetpointRequest request, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();
        if (channel.Metric != DeviceChannelMetric.SetpointTemperature || channel.Direction != ChannelDirection.ReadWrite)
            return BadRequest("Channel is not a writable SetpointTemperature channel");

        await command.SetTemperatureAsync(channel.HaEntityId, request.Temperature);
        return Accepted();
    }

    /// <summary>Writes a HvacMode or FanMode channel's underlying HA climate mode. Rejects a mode outside the channel's AvailableOptions (when known) before calling HA, rather than forwarding whatever the client sent.</summary>
    [HttpPost("{id:guid}/channels/{channelId:guid}/mode")]
    public async Task<IActionResult> SetMode(Guid id, Guid channelId, ChannelModeRequest request, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();
        if (channel.Metric is not (DeviceChannelMetric.HvacMode or DeviceChannelMetric.FanMode) || channel.Direction != ChannelDirection.ReadWrite)
            return BadRequest("Channel is not a writable HvacMode or FanMode channel");

        var options = ChannelOptionsJson.Deserialize(channel.AvailableOptions);
        if (options is not null && !options.Contains(request.Mode))
            return BadRequest($"'{request.Mode}' is not one of this channel's available options: {string.Join(", ", options)}");

        await (channel.Metric == DeviceChannelMetric.HvacMode
            ? command.SetHvacModeAsync(channel.HaEntityId, request.Mode)
            : command.SetFanModeAsync(channel.HaEntityId, request.Mode));
        return Accepted();
    }

    /// <summary>Re-reads a HvacMode/FanMode channel's AvailableOptions from HA's live hvac_modes/fan_modes attributes - for a hand-added channel that skipped Discovery, or one whose supported modes changed in HA after import.</summary>
    [HttpPost("{id:guid}/channels/{channelId:guid}/refresh-options")]
    public async Task<ActionResult<DeviceChannelDto>> RefreshOptions(Guid id, Guid channelId, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();
        if (channel.Metric is not (DeviceChannelMetric.HvacMode or DeviceChannelMetric.FanMode))
            return BadRequest("Channel is not a HvacMode or FanMode channel");

        var state = await stateReader.TryGetStateAsync(channel.HaEntityId, ct);
        var attribute = channel.Metric == DeviceChannelMetric.HvacMode ? "hvac_modes" : "fan_modes";
        channel.AvailableOptions = ChannelOptionsJson.Serialize(ThermostatChannelBuilder.ReadOptions(state, attribute));
        await db.SaveChangesAsync(ct);

        var latest = await ChannelLatestValues.GetLatestAsync(db, [channel.Id], ct);
        return ToDto(channel, latest.GetValueOrDefault(channel.Id));
    }

    /// <summary>Activates a Scene channel's underlying HA scene. Scenes are stateless triggers - there's no resulting channel value to reflect, unlike SetPower/SetSetpoint/SetMode.</summary>
    [HttpPost("{id:guid}/channels/{channelId:guid}/trigger-scene")]
    public async Task<IActionResult> TriggerScene(Guid id, Guid channelId, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();
        if (channel.Metric != DeviceChannelMetric.Scene) return BadRequest("Channel is not a Scene channel");

        await command.TriggerSceneAsync(channel.HaEntityId);
        return Accepted();
    }

    /// <summary>Triggers a one-time BackfillChannelHistory job run to pull [request.From, request.To) of HA history for this device's channels.</summary>
    [HttpPost("{id:guid}/backfill")]
    public async Task<IActionResult> Backfill(Guid id, BackfillRequest request, CancellationToken ct)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == id, ct)) return NotFound();
        if (request.From >= request.To) return BadRequest("from must be before to");
        if (request.To > DateTimeOffset.UtcNow + ClockSkewTolerance) return BadRequest("to cannot be in the future");

        var data = new JobDataMap
        {
            { "deviceId", id.ToString() },
            { "from", request.From.ToString("O") },
            { "to", request.To.ToString("O") },
        };
        await scheduler.TriggerJob(new JobKey(BackfillChannelHistory.Name, BackfillChannelHistory.Group), data, ct);
        return Accepted();
    }

    /// <summary>Bucketed history for every channel on this device, for the admin history graph's device-level modal (small multiples sharing one time axis).</summary>
    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<DeviceHistoryDto>> GetHistory(
        Guid id, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] double? bucketMinutes, CancellationToken ct)
    {
        var channels = await db.DeviceChannels.AsNoTracking().Where(c => c.DeviceId == id).ToListAsync(ct);
        if (channels.Count == 0 && !await db.Devices.AnyAsync(d => d.Id == id, ct)) return NotFound();

        var window = BuildHistoryWindow(from, to, bucketMinutes, out var error);
        if (error is not null) return BadRequest(error);

        var history = await ChannelHistoryQuery.GetAsync(db, channels, window.From, window.To, window.Bucket, ct);
        return new DeviceHistoryDto(id, history);
    }

    /// <summary>Bucketed history for a single channel, for the admin history graph's per-channel modal.</summary>
    [HttpGet("{id:guid}/channels/{channelId:guid}/history")]
    public async Task<ActionResult<ChannelHistoryDto>> GetChannelHistory(
        Guid id, Guid channelId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] double? bucketMinutes, CancellationToken ct)
    {
        var channel = await db.DeviceChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == channelId && c.DeviceId == id, ct);
        if (channel is null) return NotFound();

        var window = BuildHistoryWindow(from, to, bucketMinutes, out var error);
        if (error is not null) return BadRequest(error);

        var history = await ChannelHistoryQuery.GetAsync(db, [channel], window.From, window.To, window.Bucket, ct);
        return history[0];
    }

    private static (DateTimeOffset From, DateTimeOffset To, TimeSpan Bucket) BuildHistoryWindow(
        DateTimeOffset? from, DateTimeOffset? to, double? bucketMinutes, out string? error)
    {
        error = null;
        var resolvedTo = to ?? DateTimeOffset.UtcNow;
        var resolvedFrom = from ?? resolvedTo.AddHours(-24);
        var bucket = bucketMinutes is > 0 ? TimeSpan.FromMinutes(bucketMinutes.Value) : TimeSpan.FromMinutes(15);

        if (resolvedFrom >= resolvedTo) error = "from must be before to";
        else if (resolvedTo > DateTimeOffset.UtcNow + ClockSkewTolerance) error = "to cannot be in the future";

        return (resolvedFrom, resolvedTo, bucket);
    }

    private static DeviceDto ToDto(EfDevice d, IReadOnlyDictionary<Guid, ChannelLatestValue> latest) => new(
        d.Id, d.Name, d.Kind, d.ZoneId, d.HaDeviceId, d.Enabled,
        d.Channels.Select(c => ToDto(c, latest.GetValueOrDefault(c.Id))).ToList());

    private static DeviceChannelDto ToDto(EfDeviceChannel c, ChannelLatestValue latest) =>
        new(c.Id, c.Metric, c.HaEntityId, c.HaAttribute, c.Direction, latest.Value, latest.State, latest.Timestamp,
            ChannelOptionsJson.Deserialize(c.AvailableOptions));
}
