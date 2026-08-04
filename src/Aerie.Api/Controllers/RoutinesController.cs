using Aerie.Api.Ef;
using Aerie.Api.Models.Routines;
using Aerie.Api.Services.DeviceMapping;
using Aerie.Api.Services.Routines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Controllers;

/// <summary>
/// Routine CRUD, plus the manual trigger endpoint that runs a Routine's
/// actions via RoutineActionExecutor. Mirrors ZonesController's admin CRUD
/// shape; unlike Device/DeviceChannel's separate sub-resource endpoints, a
/// Routine's Actions are embedded in the write request and replaced wholesale
/// on every Create/Update - see RoutineWriteRequest.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class RoutinesController(AerieContext db, IHomeAssistantCommandService command) : ControllerBase
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

    [HttpPost]
    public async Task<ActionResult<RoutineDto>> Create(RoutineWriteRequest request, CancellationToken ct)
    {
        var routine = new EfRoutine
        {
            Name = request.Name,
            Description = request.Description,
            SortOrder = request.SortOrder,
            Included = request.Included,
            Actions = request.Actions.Select(ToAction).ToList(),
        };
        db.Routines.Add(routine);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = routine.Id }, ToDto(routine));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RoutineDto>> Update(Guid id, RoutineWriteRequest request, CancellationToken ct)
    {
        var routine = await db.Routines.Include(r => r.Actions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (routine is null) return NotFound();

        routine.Name = request.Name;
        routine.Description = request.Description;
        routine.SortOrder = request.SortOrder;
        routine.Included = request.Included;

        db.RoutineActions.RemoveRange(routine.Actions);
        routine.Actions = request.Actions.Select(ToAction).ToList();

        await db.SaveChangesAsync(ct);
        return ToDto(routine);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var routine = await db.Routines.FindAsync([id], ct);
        if (routine is null) return NotFound();
        db.Routines.Remove(routine);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Runs the Routine's actions against Home Assistant now, in SortOrder.</summary>
    [HttpPost("{id:guid}/trigger")]
    public async Task<IActionResult> Trigger(Guid id, CancellationToken ct)
    {
        var routine = await db.Routines.AsNoTracking()
            .Include(r => r.Actions).ThenInclude(a => a.Channel)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (routine is null) return NotFound();

        await RoutineActionExecutor.ExecuteAsync(routine.Actions, command, ct);
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
        routine.Id, routine.Name, routine.Description, routine.SortOrder, routine.Included,
        routine.Actions
            .OrderBy(a => a.SortOrder)
            .Select(a => new RoutineActionDto(a.Id, a.ChannelId, a.Kind, a.Value, a.SortOrder))
            .ToList());
}
