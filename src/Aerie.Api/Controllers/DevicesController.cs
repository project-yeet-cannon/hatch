using Aerie.Api.Ef;
using Aerie.Api.Jobs;
using Aerie.Api.Models.DeviceMapping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Quartz;

namespace Aerie.Api.Controllers;

/// <summary>CRUD for Devices and their Channels, including assigning a Device to a Zone via Update (docs/device-architecture.md Phase 2).</summary>
[ApiController]
[Route("api/[controller]")]
public class DevicesController(AerieContext db, IScheduler scheduler) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<DeviceDto>> GetAll(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Include(d => d.Channels).ToListAsync(ct);
        return devices.Select(ToDto).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DeviceDto>> Get(Guid id, CancellationToken ct)
    {
        var device = await db.Devices.AsNoTracking().Include(d => d.Channels).FirstOrDefaultAsync(d => d.Id == id, ct);
        return device is null ? NotFound() : ToDto(device);
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
        return CreatedAtAction(nameof(Get), new { id = device.Id }, ToDto(device));
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
        return ToDto(device);
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
        };
        db.DeviceChannels.Add(channel);
        await db.SaveChangesAsync(ct);
        return ToDto(channel);
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
        await db.SaveChangesAsync(ct);
        return ToDto(channel);
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

    /// <summary>Triggers a one-time BackfillChannelHistory job run to pull [request.From, request.To) of HA history for this device's channels.</summary>
    [HttpPost("{id:guid}/backfill")]
    public async Task<IActionResult> Backfill(Guid id, BackfillRequest request, CancellationToken ct)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == id, ct)) return NotFound();
        if (request.From >= request.To) return BadRequest("from must be before to");
        if (request.To > DateTimeOffset.UtcNow) return BadRequest("to cannot be in the future");

        var data = new JobDataMap
        {
            { "deviceId", id.ToString() },
            { "from", request.From.ToString("O") },
            { "to", request.To.ToString("O") },
        };
        await scheduler.TriggerJob(new JobKey(BackfillChannelHistory.Name, BackfillChannelHistory.Group), data, ct);
        return Accepted();
    }

    private static DeviceDto ToDto(EfDevice d) => new(
        d.Id, d.Name, d.Kind, d.ZoneId, d.HaDeviceId, d.Enabled,
        d.Channels.Select(ToDto).ToList());

    private static DeviceChannelDto ToDto(EfDeviceChannel c) => new(c.Id, c.Metric, c.HaEntityId, c.HaAttribute, c.Direction);
}
