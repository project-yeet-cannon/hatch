using Hatch.Api.Ef;
using Hatch.Api.Models.ClimateControl;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Controllers;

/// <summary>
/// Read-only view of the command ledger and the manual overrides derived from
/// it (docs/climate-brain-architecture.md Phase 1). Read-only on purpose:
/// commands are written by ClimateCommandService as a side effect of actually
/// actuating something, never posted directly, so there is no way to enter a
/// row for an actuation that didn't happen.
///
/// The admin decision-log UI arrives with the controller in Phase 4; this
/// exists first so the ledger can be verified in a running deployment without
/// reaching for psql.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CommandsController(AppDbContext db, TimeProvider time) : ControllerBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    /// <summary>Most recent commands first, optionally narrowed to one device or channel.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<CommandDto>> GetAll(
        [FromQuery] Guid? deviceId, [FromQuery] Guid? channelId, [FromQuery] CommandSource? source,
        [FromQuery] int? limit, CancellationToken ct)
    {
        var query = db.Commands.AsNoTracking().Include(c => c.Channel).ThenInclude(ch => ch!.Device).AsQueryable();

        if (channelId is { } cid) query = query.Where(c => c.ChannelId == cid);
        if (deviceId is { } did) query = query.Where(c => c.Channel!.DeviceId == did);
        if (source is { } src) query = query.Where(c => c.Source == src);

        var commands = await query
            .OrderByDescending(c => c.RequestedAt)
            .Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))
            .ToListAsync(ct);

        return commands.Select(ToDto).ToList();
    }

    /// <summary>Detected manual overrides, most recent first. IsActive marks the ones still holding the controller off their device.</summary>
    [HttpGet("overrides")]
    public async Task<IReadOnlyList<ControlOverrideDto>> GetOverrides(
        [FromQuery] bool activeOnly, [FromQuery] int? limit, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var query = db.ControlOverrides.AsNoTracking()
            .Include(o => o.Device)
            .Include(o => o.Channel)
            .AsQueryable();

        if (activeOnly) query = query.Where(o => o.SuppressedUntil > now);

        var overrides = await query
            .OrderByDescending(o => o.DetectedAt)
            .Take(Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit))
            .ToListAsync(ct);

        return overrides.Select(o => new ControlOverrideDto(
            o.Id, o.DeviceId, o.Device?.Name, o.ChannelId, o.Channel?.HaEntityId ?? "", o.CommandId,
            o.DetectedAt, o.ExpectedState, o.ObservedState, o.SuppressedUntil, o.SuppressedUntil > now)).ToList();
    }

    private static CommandDto ToDto(EfCommand c) => new(
        c.Id, c.ChannelId, c.Channel?.DeviceId, c.Channel?.Device?.Name, c.Channel?.HaEntityId ?? "",
        c.Channel?.Metric ?? default, c.Kind, c.Value, c.Source, c.Reason, c.DecisionId,
        c.RequestedAt, c.DispatchedAt, c.Outcome, c.Error, c.ConfirmedAt, c.ConfirmedValue, c.OverriddenAt);
}
