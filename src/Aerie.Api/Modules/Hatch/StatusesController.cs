using Aerie.Api.Common;
using Aerie.Api.Ef;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// The board's columns. Rows rather than an enum because the operator adds and
/// reorders them from a page, and an enum would make "add a review column" a
/// deploy.
/// </summary>
[ApiController]
[Route("api/hatch/statuses")]
[RequireAdmin(AcceptScope = ApiKeyScopes.Hatch)]
public class StatusesController(HatchContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StatusDto>>> GetStatuses(CancellationToken ct)
    {
        var statuses = await db.Statuses.AsNoTracking()
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Id)
            .Select(s => new StatusDto(s.Id, s.Name, s.SortOrder, s.IsTerminal, s.IsDeferred, s.Color))
            .ToListAsync(ct);

        return statuses;
    }

    [HttpPost]
    public async Task<ActionResult<StatusDto>> CreateStatus(StatusCreateRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (Invalid(name) is { } error) return BadRequest(error);
        if (InvalidColor(request.Color) is { } colorError) return BadRequest(colorError);
        if (await db.Statuses.AnyAsync(s => s.Name == name, ct)) return Conflict($"there is already a \"{name}\" column");

        var status = new EfHatchStatus
        {
            Name = name!,
            Color = request.Color is null ? EfHatchStatus.DefaultColor : EfHatchStatus.NormalizeColor(request.Color),
            // A column with no stated position goes on the right, a gap past
            // the last one - the same sparse trick ranks use a size up, so the
            // next insertion between two columns is a single write.
            SortOrder = request.SortOrder ?? await NextSortOrderAsync(ct),
            IsTerminal = request.IsTerminal ?? false,
            IsDeferred = request.IsDeferred ?? false,
        };
        db.Statuses.Add(status);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(
            nameof(GetStatuses),
            new StatusDto(status.Id, status.Name, status.SortOrder, status.IsTerminal, status.IsDeferred, status.Color));
    }

    [HttpPatch("{id:int}")]
    public async Task<ActionResult<StatusDto>> PatchStatus(int id, StatusPatchRequest request, CancellationToken ct)
    {
        var status = await db.Statuses.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (status is null) return NotFound();

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (Invalid(name) is { } error) return BadRequest(error);
            if (await db.Statuses.AnyAsync(s => s.Name == name && s.Id != id, ct))
                return Conflict($"there is already a \"{name}\" column");
            status.Name = name;
        }

        if (request.Color is not null)
        {
            if (InvalidColor(request.Color) is { } colorError) return BadRequest(colorError);
            status.Color = EfHatchStatus.NormalizeColor(request.Color);
        }

        if (request.SortOrder is { } sortOrder) status.SortOrder = sortOrder;
        if (request.IsTerminal is { } terminal) status.IsTerminal = terminal;
        if (request.IsDeferred is { } deferred) status.IsDeferred = deferred;

        await db.SaveChangesAsync(ct);
        return new StatusDto(status.Id, status.Name, status.SortOrder, status.IsTerminal, status.IsDeferred, status.Color);
    }

    /// <summary>
    /// Deletes an unused column. The issues in a column are the reason it
    /// cannot simply go - moving them somewhere on the operator's behalf would
    /// be this endpoint deciding that a dozen tickets are now "todo".
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteStatus(int id, CancellationToken ct)
    {
        var status = await db.Statuses.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (status is null) return NotFound();

        var count = await db.Issues.CountAsync(i => i.StatusId == id, ct);
        if (count > 0)
            return Conflict($"\"{status.Name}\" still holds {count} issue{(count == 1 ? "" : "s")}");

        // Not in the plan's table, and here because the alternative is a state
        // the UI cannot get out of: a board with no columns has nowhere to put
        // a new issue, so deleting the last one would leave Hatch unusable
        // until somebody opened psql.
        if (await db.Statuses.CountAsync(ct) == 1)
            return Conflict("a board needs at least one column");

        db.Statuses.Remove(status);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<int> NextSortOrderAsync(CancellationToken ct)
    {
        var last = await db.Statuses.OrderByDescending(s => s.SortOrder).Select(s => (int?)s.SortOrder).FirstOrDefaultAsync(ct);
        return (last ?? 0) + 10;
    }

    /// <summary>
    /// Null is "leave it alone" here as everywhere; anything else has to be a
    /// colour this can store, said in one shape - see
    /// <see cref="EfHatchStatus.IsValidColor"/>.
    /// </summary>
    private static string? InvalidColor(string? color) =>
        color is null || EfHatchStatus.IsValidColor(color)
            ? null
            : $"a column colour is a hex value like #6b7280 - not \"{color}\"";

    private static string? Invalid(string? name) => name switch
    {
        null or "" => "a column needs a name",
        { Length: > EfHatchStatus.MaxNameLength } => $"a column name is at most {EfHatchStatus.MaxNameLength} characters",
        _ => null,
    };
}
