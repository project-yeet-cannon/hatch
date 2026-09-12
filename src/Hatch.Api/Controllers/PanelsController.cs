using Hatch.Api.Common;
using System.Globalization;
using Hatch.Api.Ef;
using Hatch.Api.Models.Panels;
using Hatch.Api.Services.ClimateControl;
using Hatch.Api.Services.Panels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Controllers;

/// <summary>
/// Panel CRUD for the admin app, plus the kiosk's state read and the two write
/// endpoints an open overlay uses. Shaped like RoutinesController: a Panel's
/// Items (and their Bindings) are embedded in the write request and replaced
/// wholesale on every Create/Update - see PanelWriteRequest.
///
/// The writes are item-scoped rather than channel-scoped on purpose. The panel
/// is the boundary: a wall tablet in a hallway can act on what an admin
/// deliberately put on a panel and cannot address an arbitrary channel by id,
/// which is a smaller surface than DevicesController's channel writes.
/// Everything still goes out through IClimateCommandService, so panel
/// actuations land in the same ledger, under the same guards, as every other
/// write Hatch makes (docs/climate-brain-architecture.md).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PanelsController(AppDbContext db, IPanelService panels, IClimateCommandService commands) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<PanelDto>> GetAll(CancellationToken ct)
        => (await QueryWithItems(db.Panels.AsNoTracking())
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.Name)
                .ToListAsync(ct))
            .Select(ToDto)
            .ToList();

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PanelDto>> Get(Guid id, CancellationToken ct)
    {
        var panel = await QueryWithItems(db.Panels.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == id, ct);
        return panel is null ? NotFound() : ToDto(panel);
    }

    /// <summary>
    /// Live per-item state for one open overlay, polled every ~5s while it is
    /// open. Separate from the dashboard snapshot because that poll is 60s and
    /// carries every panel whether or not anyone opened one - see PanelSummary.
    /// </summary>
    [HttpGet("{id:guid}/state")]
    public async Task<ActionResult<PanelStateDto>> GetState(Guid id, CancellationToken ct)
    {
        var state = await panels.GetStateAsync(id, ct);
        return state is null ? NotFound() : state;
    }

    [RequireAdmin]
    [HttpPost]
    public async Task<ActionResult<PanelDto>> Create(PanelWriteRequest request, CancellationToken ct)
    {
        var items = request.Items.Select(ToItem).ToList();
        if (await ValidationErrorAsync(items, ct) is { } error) return BadRequest(error);

        var panel = new EfPanel
        {
            Name = request.Name,
            Description = request.Description,
            Icon = request.Icon,
            Color = request.Color,
            SortOrder = request.SortOrder,
            Included = request.Included,
            Items = items,
        };
        db.Panels.Add(panel);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = panel.Id }, ToDto(panel));
    }

    [RequireAdmin]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PanelDto>> Update(Guid id, PanelWriteRequest request, CancellationToken ct)
    {
        var panel = await QueryWithItems(db.Panels).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (panel is null) return NotFound();

        var items = request.Items.Select(ToItem).ToList();
        if (await ValidationErrorAsync(items, ct) is { } error) return BadRequest(error);

        panel.Name = request.Name;
        panel.Description = request.Description;
        panel.Icon = request.Icon;
        panel.Color = request.Color;
        panel.SortOrder = request.SortOrder;
        panel.Included = request.Included;

        // The bindings go with their items by cascade in the database, but the
        // tracked graph needs telling too, or EF tries to orphan them.
        db.PanelControlBindings.RemoveRange(panel.Items.SelectMany(i => i.Bindings));
        db.PanelItems.RemoveRange(panel.Items);
        panel.Items = items;

        await db.SaveChangesAsync(ct);
        return ToDto(panel);
    }

    [RequireAdmin]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var panel = await db.Panels.FindAsync([id], ct);
        if (panel is null) return NotFound();
        db.Panels.Remove(panel);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Turns one control on or off. A Switch, and a Thermostat with a Power
    /// binding, get SetPower; a Thermostat with only a Mode channel gets
    /// SetHvacMode to its OnMode ("cool" on the air conditioner, "heat" on a
    /// radiator) or to "off", which is what makes both devices present the
    /// single On/Off the kiosk renders.
    /// </summary>
    [HttpPost("{id:guid}/items/{itemId:guid}/power")]
    public async Task<IActionResult> SetPower(Guid id, Guid itemId, PanelPowerRequest request, CancellationToken ct)
    {
        var (panel, item) = await LoadControlAsync(id, itemId, ct);
        if (panel is null || item is null) return NotFound();
        var kind = item.ControlKind!.Value;

        CommandRequest command;
        if (item.Bindings.FirstOrDefault(b => b.Role == ControlRole.Power) is { } power)
        {
            command = Command(power.ChannelId, CommandKind.SetPower, request.On ? "true" : "false", panel, item);
        }
        else if (kind == ControlKind.Thermostat && item.Bindings.FirstOrDefault(b => b.Role == ControlRole.Mode) is { } mode)
        {
            // PanelBindingRules requires OnMode whenever Mode is bound, so this
            // is only null for a row written outside the API.
            if (string.IsNullOrWhiteSpace(item.OnMode))
                return BadRequest("This thermostat has no OnMode, so its mode channel can't be switched.");

            command = Command(mode.ChannelId, CommandKind.SetHvacMode, request.On ? item.OnMode : "off", panel, item);
        }
        else
        {
            return BadRequest($"This {kind} control has no on/off channel bound.");
        }

        return Dispatched(await commands.DispatchAsync(command, ct));
    }

    /// <summary>
    /// Sets a thermostat's target temperature. The requested value is clamped
    /// to the control's bounds and snapped to its step server-side: the kiosk
    /// clamps too, but the panel is the boundary, and the endpoint cannot
    /// assume the client that called it is the one we shipped.
    /// </summary>
    [HttpPost("{id:guid}/items/{itemId:guid}/setpoint")]
    public async Task<IActionResult> SetSetpoint(Guid id, Guid itemId, PanelSetpointRequest request, CancellationToken ct)
    {
        var (panel, item) = await LoadControlAsync(id, itemId, ct);
        if (panel is null || item is null) return NotFound();
        var kind = item.ControlKind!.Value;

        if (kind != ControlKind.Thermostat)
            return BadRequest($"A {kind} control has no setpoint.");

        if (item.Bindings.FirstOrDefault(b => b.Role == ControlRole.Setpoint) is not { } setpoint)
            return BadRequest("This thermostat has no setpoint channel bound.");

        var value = Snap(request.ValueF, item.MinF ?? PanelDefaults.MinF, item.MaxF ?? PanelDefaults.MaxF, item.StepF ?? PanelDefaults.StepF);
        var command = Command(setpoint.ChannelId, CommandKind.SetTemperature, value.ToString(CultureInfo.InvariantCulture), panel, item);

        return Dispatched(await commands.DispatchAsync(command, ct));
    }

    /// <summary>
    /// Clamps into the control's range, snaps to the nearest step measured from
    /// the minimum, and clamps again - the snap can push the top of an
    /// unevenly-divided range back over Max, and a command above the bound is
    /// exactly what the clamp exists to prevent.
    /// </summary>
    private static decimal Snap(decimal value, decimal min, decimal max, decimal step)
    {
        var clamped = Math.Clamp(value, min, max);
        var snapped = min + Math.Round((clamped - min) / step, MidpointRounding.AwayFromZero) * step;
        return Math.Clamp(snapped, min, max);
    }

    /// <summary>
    /// The panel and the control an item-scoped write names, or nulls when
    /// either is missing. A routine item reads as missing rather than as a bad
    /// request: these endpoints address controls, so an id that isn't one names
    /// nothing they can act on - the kiosk triggers a routine item through
    /// /api/routines/{id}/trigger instead.
    /// </summary>
    private async Task<(EfPanel? Panel, EfPanelItem? Item)> LoadControlAsync(Guid panelId, Guid itemId, CancellationToken ct)
    {
        var panel = await db.Panels.AsNoTracking()
            .Include(p => p.Items).ThenInclude(i => i.Bindings)
            .FirstOrDefaultAsync(p => p.Id == panelId, ct);
        if (panel is null) return (null, null);

        var item = panel.Items.FirstOrDefault(i => i.Id == itemId);
        return item is null || item.Kind != PanelItemKind.Control || item.ControlKind is null
            ? (null, null)
            : (panel, item);
    }

    /// <summary>Names both halves of what a person touched - "Panel 'Climate' - Air conditioner" - so the ledger says which surface an actuation came from, not just that a person was involved.</summary>
    private static CommandRequest Command(Guid channelId, CommandKind kind, string? value, EfPanel panel, EfPanelItem item)
        => new(channelId, kind, value, CommandSource.Human,
            $"Panel '{panel.Name}' — {(string.IsNullOrWhiteSpace(item.Label) ? item.ControlKind?.ToString() ?? "control" : item.Label)}");

    /// <summary>Maps a dispatch outcome onto a status the same way the routine endpoints do: what the guards refused is the caller's fault, what Home Assistant refused is not.</summary>
    private IActionResult Dispatched(CommandResult result) => result.Outcome switch
    {
        CommandOutcome.Rejected => BadRequest(result.Error),
        CommandOutcome.Failed => StatusCode(StatusCodes.Status502BadGateway, result.Error),
        _ => NoContent(),
    };

    /// <summary>
    /// Runs PanelBindingRules over every item in a proposed panel, against the
    /// channels the request actually names. An item whose binding points at a
    /// channel that doesn't exist fails the same rules as one pointing at the
    /// wrong metric, because the rules answer "does this item exist" rather
    /// than "did this write succeed" - an invalid panel must never reach the
    /// database, where the kiosk would then have to render it.
    /// </summary>
    private async Task<string?> ValidationErrorAsync(IReadOnlyList<EfPanelItem> items, CancellationToken ct)
    {
        var channelIds = items.SelectMany(i => i.Bindings.Select(b => b.ChannelId)).Distinct().ToList();
        var channels = await db.DeviceChannels.AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var item in items)
        {
            if (PanelBindingRules.Validate(item, channels) is { } reason) return reason;
        }

        // Checked here rather than left to the foreign key, because a bad
        // routine id is an ordinary bad request and a DbUpdateException is a
        // 500. PanelBindingRules can't answer this one - it's pure, and this
        // needs the database.
        var routineIds = items.Where(i => i.RoutineId is { } id).Select(i => i.RoutineId!.Value).Distinct().ToList();
        var known = await db.Routines.AsNoTracking().Where(r => routineIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        if (routineIds.Except(known).Any()) return "A routine item references a routine that does not exist.";

        return null;
    }

    private static IQueryable<EfPanel> QueryWithItems(IQueryable<EfPanel> query)
        => query.Include(p => p.Items).ThenInclude(i => i.Bindings);

    private static EfPanelItem ToItem(PanelItemWriteRequest request) => new()
    {
        SortOrder = request.SortOrder,
        Kind = request.Kind,
        RoutineId = request.RoutineId,
        ControlKind = request.ControlKind,
        Label = request.Label,
        Icon = request.Icon,
        Color = request.Color,
        OnMode = request.OnMode,
        MinF = request.MinF,
        MaxF = request.MaxF,
        StepF = request.StepF,
        Bindings = request.Bindings
            .Select(b => new EfPanelControlBinding { Role = b.Role, ChannelId = b.ChannelId })
            .ToList(),
    };

    private static PanelDto ToDto(EfPanel panel) => new(
        panel.Id, panel.Name, panel.Description, panel.Icon, panel.Color, panel.SortOrder, panel.Included,
        panel.Items
            .OrderBy(i => i.SortOrder)
            .Select(i => new PanelItemDto(
                i.Id, i.SortOrder, i.Kind, i.RoutineId, i.ControlKind, i.Label, i.Icon, i.Color,
                i.OnMode, i.MinF, i.MaxF, i.StepF,
                i.Bindings.Select(b => new PanelControlBindingDto(b.Id, b.Role, b.ChannelId)).ToList()))
            .ToList());
}
