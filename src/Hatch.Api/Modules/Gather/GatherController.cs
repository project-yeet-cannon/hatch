using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Modules.Gather;

/// <summary>
/// Gather's whole API: named lists and the items on them. Plain CRUD runs
/// against GatherContext directly - only the add upsert and the clear-checked
/// sweep go through IGatherService, because only they have behaviour worth
/// isolating.
/// </summary>
/// <remarks>
/// Checking an item is its own endpoint rather than a field on the edit,
/// deliberately. The two writes that collide in practice are a phone checking
/// things off in an aisle and the kitchen wall renaming one a second earlier;
/// separating them means the common collision structurally cannot clobber text,
/// instead of being a race nobody notices losing.
/// </remarks>
[ApiController]
[Route("api/gather")]
public class GatherController(GatherContext db, IGatherService gather, TimeProvider time) : ControllerBase
{
    // ---- Lists ----

    [HttpGet("lists")]
    public async Task<IReadOnlyList<ListSummaryDto>> GetLists(CancellationToken ct)
        => await db.Lists.AsNoTracking()
            .OrderBy(l => l.Name)
            .Select(l => new ListSummaryDto(
                l.Id, l.Name, l.Icon, l.Color,
                l.Items.Count(i => !i.IsChecked), l.Items.Count(i => i.IsChecked),
                l.CreatedAt, l.UpdatedAt))
            .ToListAsync(ct);

    /// <summary>The list and everything on it, in one response - what opening a list draws and what the poll loop re-fetches.</summary>
    [HttpGet("lists/{id:guid}")]
    public async Task<ActionResult<ListDto>> GetList(Guid id, CancellationToken ct)
    {
        var list = await db.Lists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (list is null) return NotFound();

        // A second query rather than a projection over the navigation, so the
        // one ordering both clients see stays one shared expression. The counts
        // then come off the rows already in hand rather than a third query.
        var items = await GatherService.InDisplayOrder(db.Items.AsNoTracking().Where(i => i.ListId == id)).ToListAsync(ct);

        return new ListDto(
            ToSummary(list, items.Count(i => !i.IsChecked), items.Count(i => i.IsChecked)),
            [.. items.Select(ToDto)]);
    }

    [HttpPost("lists")]
    public async Task<ActionResult<ListSummaryDto>> CreateList(ListWriteRequest request, CancellationToken ct)
    {
        if (Invalid(request) is { } error) return BadRequest(error);

        var now = time.GetUtcNow();
        var list = new GatherList
        {
            Name = request.Name.Trim(),
            Icon = Clean(request.Icon),
            Color = Clean(request.Color),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Lists.Add(list);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetList), new { id = list.Id }, ToSummary(list, openCount: 0, checkedCount: 0));
    }

    [HttpPut("lists/{id:guid}")]
    public async Task<ActionResult<ListSummaryDto>> UpdateList(Guid id, ListWriteRequest request, CancellationToken ct)
    {
        if (Invalid(request) is { } error) return BadRequest(error);

        var list = await db.Lists.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (list is null) return NotFound();

        list.Name = request.Name.Trim();
        list.Icon = Clean(request.Icon);
        list.Color = Clean(request.Color);
        list.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return ToSummary(
            list,
            await db.Items.CountAsync(i => i.ListId == id && !i.IsChecked, ct),
            await db.Items.CountAsync(i => i.ListId == id && i.IsChecked, ct));
    }

    /// <summary>Deleting a list takes its items with it (FK is CASCADE) - see GatherContext.</summary>
    [HttpDelete("lists/{id:guid}")]
    public async Task<IActionResult> DeleteList(Guid id, CancellationToken ct)
    {
        var list = await db.Lists.Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (list is null) return NotFound();

        db.Lists.Remove(list);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- Items ----

    /// <summary>
    /// Adds an item, or brings back the one already on the list under the same
    /// normalized name - including un-checking it. Not a 201: on a re-add
    /// nothing was created, and the client's next move is the same either way.
    /// </summary>
    [HttpPost("lists/{id:guid}/items")]
    public async Task<ActionResult<ItemDto>> AddItem(Guid id, ItemAddRequest request, CancellationToken ct)
    {
        if (Invalid(request.Name, request.Quantity, request.Note) is { } error) return BadRequest(error);
        if (!await db.Lists.AnyAsync(l => l.Id == id, ct)) return NotFound();

        return ToDto(await gather.AddItemAsync(id, request, ct));
    }

    /// <summary>
    /// Edits the text of an item. Never touches whether it's checked - that is
    /// <c>check</c>/<c>uncheck</c>, and keeping them apart is the point.
    /// </summary>
    [HttpPut("lists/{id:guid}/items/{itemId:guid}")]
    public async Task<ActionResult<ItemDto>> UpdateItem(Guid id, Guid itemId, ItemWriteRequest request, CancellationToken ct)
    {
        if (Invalid(request.Name, request.Quantity, request.Note) is { } error) return BadRequest(error);

        var item = await LoadItemAsync(id, itemId, ct);
        if (item is null) return NotFound();

        var normalized = GatherItem.Normalize(request.Name);
        if (normalized != item.NameNormalized
            && await db.Items.AnyAsync(i => i.ListId == id && i.NameNormalized == normalized, ct))
        {
            return Conflict("that item is already on this list");
        }

        item.Name = request.Name;
        item.Quantity = Clean(request.Quantity);
        item.Note = Clean(request.Note);
        Touch(item, time.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException dx) when (GatherService.IsDuplicateName(dx))
        {
            // The check above is not a lock; the index is. Same answer either way.
            return Conflict("that item is already on this list");
        }

        return ToDto(item);
    }

    [HttpPost("lists/{id:guid}/items/{itemId:guid}/check")]
    public Task<ActionResult<ItemDto>> CheckItem(Guid id, Guid itemId, CancellationToken ct)
        => SetCheckedAsync(id, itemId, isChecked: true, ct);

    [HttpPost("lists/{id:guid}/items/{itemId:guid}/uncheck")]
    public Task<ActionResult<ItemDto>> UncheckItem(Guid id, Guid itemId, CancellationToken ct)
        => SetCheckedAsync(id, itemId, isChecked: false, ct);

    [HttpDelete("lists/{id:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> DeleteItem(Guid id, Guid itemId, CancellationToken ct)
    {
        var item = await LoadItemAsync(id, itemId, ct);
        if (item is null) return NotFound();

        db.Items.Remove(item);
        if (item.List is not null) item.List.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Sweeps everything already in the cart off the list, reporting the count so the client can say what it removed.</summary>
    [HttpPost("lists/{id:guid}/clear-checked")]
    public async Task<ActionResult<ClearCheckedResult>> ClearChecked(Guid id, CancellationToken ct)
    {
        if (!await db.Lists.AnyAsync(l => l.Id == id, ct)) return NotFound();

        return new ClearCheckedResult(await gather.ClearCheckedAsync(id, ct));
    }

    private async Task<ActionResult<ItemDto>> SetCheckedAsync(Guid id, Guid itemId, bool isChecked, CancellationToken ct)
    {
        var item = await LoadItemAsync(id, itemId, ct);
        if (item is null) return NotFound();

        var now = time.GetUtcNow();
        item.IsChecked = isChecked;
        item.CheckedAt = isChecked ? now : null;
        Touch(item, now);
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    /// <summary>
    /// The item, with its list attached so a write can mark the list touched in
    /// the same save. Scoped by list id, so an item id from another list is a
    /// 404 rather than a cross-list edit.
    /// </summary>
    private Task<GatherItem?> LoadItemAsync(Guid listId, Guid itemId, CancellationToken ct) =>
        db.Items.Include(i => i.List).FirstOrDefaultAsync(i => i.Id == itemId && i.ListId == listId, ct);

    private static void Touch(GatherItem item, DateTimeOffset now)
    {
        item.UpdatedAt = now;
        if (item.List is not null) item.List.UpdatedAt = now;
    }

    private static string? Invalid(ListWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "name is required";
        if (request.Name.Trim().Length > GatherList.MaxNameLength) return $"name must be at most {GatherList.MaxNameLength} characters";
        if (request.Icon?.Trim().Length > GatherList.MaxIconLength) return $"icon must be at most {GatherList.MaxIconLength} characters";
        if (request.Color?.Trim().Length > GatherList.MaxColorLength) return $"color must be at most {GatherList.MaxColorLength} characters";
        return null;
    }

    /// <summary>
    /// Item text, shared by add and edit. Length is checked here rather than
    /// left to the database so an over-long name comes back as a 400 saying so,
    /// not a 500 out of a truncation error.
    /// </summary>
    private static string? Invalid(string name, string? quantity, string? note)
    {
        if (string.IsNullOrWhiteSpace(name)) return "name is required";
        if (name.Trim().Length > GatherItem.MaxNameLength) return $"name must be at most {GatherItem.MaxNameLength} characters";
        if (quantity?.Trim().Length > GatherItem.MaxQuantityLength) return $"quantity must be at most {GatherItem.MaxQuantityLength} characters";
        if (note?.Trim().Length > GatherItem.MaxNoteLength) return $"note must be at most {GatherItem.MaxNoteLength} characters";
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ListSummaryDto ToSummary(GatherList l, int openCount, int checkedCount) =>
        new(l.Id, l.Name, l.Icon, l.Color, openCount, checkedCount, l.CreatedAt, l.UpdatedAt);

    private static ItemDto ToDto(GatherItem i) =>
        new(i.Id, i.ListId, i.Name, i.Quantity, i.Note, i.IsChecked, i.CheckedAt, i.CreatedAt, i.UpdatedAt);
}
