using Hatch.Api.Common;
using Hatch.Api.Ef;
using Hatch.Api.Models.Routines;
using Hatch.Api.Services.ClimateControl;
using Hatch.Api.Services.Routines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Controllers;

/// <summary>
/// Routine CRUD, plus the manual trigger endpoint that runs a Routine's
/// actions via RoutineActionExecutor. Mirrors ZonesController's admin CRUD
/// shape; unlike Device/DeviceChannel's separate sub-resource endpoints, a
/// Routine's Actions are embedded in the write request and replaced wholesale
/// on every Create/Update - see RoutineWriteRequest.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RoutinesController(AppDbContext db, IClimateCommandService commands) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<RoutineDto>> GetAll(CancellationToken ct)
        => (await db.Routines.AsNoTracking()
                .Include(r => r.Actions)
                .OrderBy(r => r.SortOrder)
                .ToListAsync(ct))
            .Select(ToDto)
            .ToList();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RoutineDto>> Get(Guid id, CancellationToken ct)
    {
        var routine = await db.Routines.AsNoTracking()
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        return routine is null ? NotFound() : ToDto(routine);
    }

    [RequireAdmin]
    [HttpPost]
    public async Task<ActionResult<RoutineDto>> Create(RoutineWriteRequest request, CancellationToken ct)
    {
        var routine = new EfRoutine
        {
            Name = request.Name,
            Description = request.Description,
            Icon = request.Icon,
            Color = request.Color,
            SortOrder = request.SortOrder,
            Included = request.Included,
            IsToggle = request.IsToggle,
            Actions = request.Actions.Select(ToAction).ToList(),
        };
        db.Routines.Add(routine);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = routine.Id }, ToDto(routine));
    }

    [RequireAdmin]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RoutineDto>> Update(Guid id, RoutineWriteRequest request, CancellationToken ct)
    {
        var routine = await db.Routines.Include(r => r.Actions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (routine is null) return NotFound();

        routine.Name = request.Name;
        routine.Description = request.Description;
        routine.Icon = request.Icon;
        routine.Color = request.Color;
        routine.SortOrder = request.SortOrder;
        routine.Included = request.Included;
        routine.IsToggle = request.IsToggle;

        db.RoutineActions.RemoveRange(routine.Actions);
        routine.Actions = request.Actions.Select(ToAction).ToList();

        await db.SaveChangesAsync(ct);
        return ToDto(routine);
    }

    [RequireAdmin]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var routine = await db.Routines.FindAsync([id], ct);
        if (routine is null) return NotFound();
        db.Routines.Remove(routine);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Runs the Routine's actions against Home Assistant now, in SortOrder,
    /// through the command ledger - so a routine trigger is recorded action by
    /// action with Source = Routine, the same as anything else Hatch does to
    /// the house (docs/climate-brain-architecture.md Phase 1).
    ///
    /// Dispatch stops at the first action that doesn't succeed rather than
    /// pushing on: a routine's order is meaningful, so finishing "AC down, then
    /// fans on" after the AC step failed would leave the house in a state
    /// nobody asked for.
    /// </summary>
    [HttpPost("{id:guid}/trigger")]
    public async Task<IActionResult> Trigger(Guid id, CancellationToken ct)
    {
        var routine = await db.Routines.AsNoTracking()
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (routine is null) return NotFound();

        var requests = RoutineCommandMapper.ToCommandRequests(routine.Actions, $"Routine '{routine.Name}'");
        var results = await commands.DispatchManyAsync(requests, ct);

        var failure = results.FirstOrDefault(r => !r.Succeeded);
        if (failure.Outcome == CommandOutcome.Rejected) return BadRequest(failure.Error);
        if (failure.Outcome == CommandOutcome.Failed) return StatusCode(StatusCodes.Status502BadGateway, failure.Error);

        return NoContent();
    }

    /// <summary>
    /// The inverse of Trigger for a toggle routine (see EfRoutine.IsToggle):
    /// dispatches SetPower "false" to every SetPower action's channel, rather
    /// than re-running Actions. Used by the kiosk when a toggle routine's
    /// button reads active and gets tapped again.
    /// </summary>
    [HttpPost("{id:guid}/turn-off")]
    public async Task<IActionResult> TurnOff(Guid id, CancellationToken ct)
    {
        var routine = await db.Routines.AsNoTracking()
            .Include(r => r.Actions)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (routine is null) return NotFound();
        if (!routine.IsToggle) return BadRequest("Routine is not a toggle routine.");

        var requests = RoutineCommandMapper.ToOffCommandRequests(routine.Actions, $"Routine '{routine.Name}' (off)");
        var results = await commands.DispatchManyAsync(requests, ct);

        var failure = results.FirstOrDefault(r => !r.Succeeded);
        if (failure.Outcome == CommandOutcome.Rejected) return BadRequest(failure.Error);
        if (failure.Outcome == CommandOutcome.Failed) return StatusCode(StatusCodes.Status502BadGateway, failure.Error);

        return NoContent();
    }

    private static EfRoutineAction ToAction(RoutineActionWriteRequest request) => new()
    {
        ChannelId = request.ChannelId,
        Kind = request.Kind,
        Value = request.Value,
        SortOrder = request.SortOrder,
    };

    private static RoutineDto ToDto(EfRoutine routine) => new(
        routine.Id, routine.Name, routine.Description, routine.Icon, routine.Color, routine.SortOrder, routine.Included, routine.IsToggle,
        routine.Actions
            .OrderBy(a => a.SortOrder)
            .Select(a => new RoutineActionDto(a.Id, a.ChannelId, a.Kind, a.Value, a.SortOrder))
            .ToList());
}
