using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace Aerie.Api.Modules.Storage;

/// <summary>
/// Storage Helper's whole API: locations, crates, items, and the scan path
/// (<c>GET crates/by-code/{code}</c>) that a QR label resolves through.
/// Plain CRUD runs against StorageContext directly - only crate creation goes
/// through IStorageService, because only it has behaviour worth isolating.
/// </summary>
[ApiController]
[Route("api/storage")]
public class StorageController(StorageContext db, IStorageService storage, TimeProvider time) : ControllerBase
{
    // ---- Locations ----

    [HttpGet("locations")]
    public async Task<IReadOnlyList<LocationDto>> GetLocations(CancellationToken ct)
        => await db.Locations.AsNoTracking()
            .OrderBy(l => l.Name)
            .Select(l => new LocationDto(l.Id, l.Name, l.Description, l.Crates.Count, l.CreatedAt))
            .ToListAsync(ct);

    [HttpGet("locations/{id:guid}")]
    public async Task<ActionResult<LocationDto>> GetLocation(Guid id, CancellationToken ct)
    {
        var location = await db.Locations.AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new LocationDto(l.Id, l.Name, l.Description, l.Crates.Count, l.CreatedAt))
            .FirstOrDefaultAsync(ct);
        return location is null ? NotFound() : location;
    }

    [HttpPost("locations")]
    public async Task<ActionResult<LocationDto>> CreateLocation(LocationWriteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("name is required");

        var location = new StorageLocation
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            CreatedAt = time.GetUtcNow(),
        };
        db.Locations.Add(location);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetLocation), new { id = location.Id }, ToDto(location, crateCount: 0));
    }

    [HttpPut("locations/{id:guid}")]
    public async Task<ActionResult<LocationDto>> UpdateLocation(Guid id, LocationWriteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("name is required");

        var location = await db.Locations.Include(l => l.Crates).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (location is null) return NotFound();

        location.Name = request.Name.Trim();
        location.Description = request.Description;
        await db.SaveChangesAsync(ct);
        return ToDto(location, location.Crates.Count);
    }

    /// <summary>Deleting a location leaves its crates in place, unplaced (FK is SET NULL) - see StorageContext.</summary>
    [HttpDelete("locations/{id:guid}")]
    public async Task<IActionResult> DeleteLocation(Guid id, CancellationToken ct)
    {
        var location = await db.Locations.Include(l => l.Crates).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (location is null) return NotFound();

        db.Locations.Remove(location);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static LocationDto ToDto(StorageLocation l, int crateCount) =>
        new(l.Id, l.Name, l.Description, crateCount, l.CreatedAt);

    // ---- Crates ----

    [HttpGet("crates")]
    public async Task<IReadOnlyList<CrateDto>> GetCrates([FromQuery] Guid? locationId, CancellationToken ct)
    {
        var query = db.Crates.AsNoTracking();
        if (locationId is { } id) query = query.Where(c => c.LocationId == id);

        var crates = await query
            .OrderBy(c => c.Location == null ? 1 : 0)
            .ThenBy(c => c.Location!.Name)
            .ThenBy(c => c.Code)
            .Select(c => new { Crate = c, LocationName = c.Location == null ? null : c.Location.Name, ItemCount = c.Items.Count })
            .ToListAsync(ct);

        return [.. crates.Select(c => ToDto(c.Crate, c.LocationName, c.ItemCount))];
    }

    [HttpGet("crates/{id:guid}")]
    public async Task<ActionResult<CrateDetailDto>> GetCrate(Guid id, CancellationToken ct)
    {
        var detail = await LoadCrateAsync(c => c.Id == id, ct);
        return detail is null ? NotFound() : detail;
    }

    /// <summary>
    /// The scan path. Accepts whatever a person types or a QR carries - dashed,
    /// lowercase, or with Crockford's ambiguous letters (I/L/O) in place of the
    /// digits they look like; see CrateCode.Normalize.
    /// </summary>
    [HttpGet("crates/by-code/{code}")]
    public async Task<ActionResult<CrateDetailDto>> GetCrateByCode(string code, CancellationToken ct)
    {
        var normalized = CrateCode.Normalize(code);
        if (normalized is null) return NotFound();

        var detail = await LoadCrateAsync(c => c.Code == normalized, ct);
        return detail is null ? NotFound() : detail;
    }

    [HttpPost("crates")]
    public async Task<ActionResult<CrateDetailDto>> CreateCrate(CrateWriteRequest request, CancellationToken ct)
    {
        if (await MissingLocation(request.LocationId, ct)) return BadRequest("locationId does not exist");

        var crate = (await storage.CreateCratesAsync(1, request, ct))[0];
        var detail = new CrateDetailDto(ToDto(crate, await LocationNameAsync(crate.LocationId, ct), itemCount: 0), []);
        return CreatedAtAction(nameof(GetCrate), new { id = crate.Id }, detail);
    }

    /// <summary>
    /// Mints N blank crates for a label sheet. This is the workflow that matters:
    /// print, tape onto empty boxes, then scan each one as it gets filled -
    /// naming a crate before it physically exists is backwards.
    /// </summary>
    [HttpPost("crates/batch")]
    public async Task<ActionResult<IReadOnlyList<CrateDto>>> CreateCrateBatch(CrateBatchRequest request, CancellationToken ct)
    {
        if (request.Count < 1 || request.Count > StorageService.MaxBatchCount)
            return BadRequest($"count must be between 1 and {StorageService.MaxBatchCount}");

        var crates = await storage.CreateCratesAsync(request.Count, initial: null, ct);
        return Ok(crates.Select(c => ToDto(c, locationName: null, itemCount: 0)).ToList());
    }

    [HttpPut("crates/{id:guid}")]
    public async Task<ActionResult<CrateDto>> UpdateCrate(Guid id, CrateWriteRequest request, CancellationToken ct)
    {
        if (await MissingLocation(request.LocationId, ct)) return BadRequest("locationId does not exist");

        var crate = await db.Crates.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crate is null) return NotFound();

        // Code is deliberately not writable: it's taped to a box.
        crate.Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        crate.LocationId = request.LocationId;
        crate.Notes = request.Notes;
        crate.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        // Counted rather than Include'd: the count is all this needs, and loading
        // the items would drag a search document along for each one.
        return ToDto(crate, await LocationNameAsync(crate.LocationId, ct), await db.Items.CountAsync(i => i.CrateId == id, ct));
    }

    /// <summary>Deleting a crate takes its items with it (FK is CASCADE) and leaves its location alone.</summary>
    [HttpDelete("crates/{id:guid}")]
    public async Task<IActionResult> DeleteCrate(Guid id, CancellationToken ct)
    {
        var crate = await db.Crates.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (crate is null) return NotFound();

        db.Crates.Remove(crate);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<CrateDetailDto?> LoadCrateAsync(Expression<Func<Crate, bool>> match, CancellationToken ct)
    {
        var row = await db.Crates.AsNoTracking()
            .Where(match)
            .Select(c => new
            {
                Crate = c,
                LocationName = c.Location == null ? null : c.Location.Name,
                // Projected to the DTO rather than loaded as entities: an Item
                // carries a tsvector, and this is the scan path - the one response
                // in the app that has to be small.
                Items = c.Items.OrderBy(i => i.Name)
                    .Select(i => new ItemDto(i.Id, i.CrateId, i.Name, i.Quantity, i.Notes, i.CreatedAt, i.UpdatedAt))
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? null
            : new CrateDetailDto(ToDto(row.Crate, row.LocationName, row.Items.Count), row.Items);
    }

    private async Task<bool> MissingLocation(Guid? locationId, CancellationToken ct) =>
        locationId is { } id && !await db.Locations.AnyAsync(l => l.Id == id, ct);

    private async Task<string?> LocationNameAsync(Guid? locationId, CancellationToken ct) =>
        locationId is { } id
            ? await db.Locations.AsNoTracking().Where(l => l.Id == id).Select(l => l.Name).FirstOrDefaultAsync(ct)
            : null;

    private static CrateDto ToDto(Crate c, string? locationName, int itemCount) =>
        new(c.Id, c.Code, CrateCode.Format(c.Code), c.Label, c.LocationId, locationName, c.Notes, itemCount, c.CreatedAt, c.UpdatedAt);

    // ---- Items ----

    /// <summary>
    /// The flat "where is the drill" index across every crate. Each row carries
    /// its crate and location, so the list screen needs nothing else.
    /// </summary>
    [HttpGet("items")]
    public async Task<IReadOnlyList<ItemIndexRow>> GetItems([FromQuery] Guid? crateId, CancellationToken ct)
    {
        var query = db.Items.AsNoTracking();
        if (crateId is { } id) query = query.Where(i => i.CrateId == id);

        return await IndexRowsAsync(query.OrderBy(i => i.Name), ct);
    }

    /// <summary>
    /// Full-text search across the same index, returning the same rows - so the
    /// screen that shows the whole list and the screen that shows matches are one
    /// screen. Empty or punctuation-only queries return nothing rather than
    /// everything: the caller already has the full list from <c>GET items</c>.
    /// </summary>
    /// <remarks>
    /// Two things about the query are deliberate, and both come from how people
    /// actually look for a box:
    /// <list type="bullet">
    /// <item>The document is the item's stored vector <b>concatenated with its
    /// crate and location text</b>, because "drill garage" has to match a drill
    /// in the garage - and a generated column can only ever see its own row.</item>
    /// <item>Results are ordered by name, not <c>ts_rank</c>. With prefix terms
    /// that all have to match, relevance is close to binary; a stable alphabetical
    /// list is easier to scan than a ranking nobody can perceive.</item>
    /// </list>
    /// </remarks>
    [HttpGet("search")]
    public async Task<IReadOnlyList<ItemIndexRow>> Search([FromQuery] string? q, CancellationToken ct)
    {
        var tsquery = SearchQuery.ToTsQuery(q);
        if (tsquery is null) return [];

        var matches = db.Items.AsNoTracking().Where(i =>
            i.SearchVector
                .Concat(EF.Functions.ToTsVector(SearchQuery.Config,
                    (i.Crate!.Label ?? "") + " " +
                    // The code three ways - bare, and each half on its own - so a
                    // code read off a label finds its contents whether it's typed
                    // "D2QDYM", "D2Q-DYM" or just the half that's still legible.
                    i.Crate!.Code + " " +
                    i.Crate!.Code.Substring(0, CrateCode.GroupSize) + " " +
                    i.Crate!.Code.Substring(CrateCode.GroupSize) + " " +
                    (i.Crate!.Location == null ? "" : i.Crate!.Location.Name)))
                .Matches(EF.Functions.ToTsQuery(SearchQuery.Config, tsquery)));

        return await IndexRowsAsync(matches.OrderBy(i => i.Name), ct, SearchQuery.MaxResults);
    }

    /// <summary>
    /// Materialises index rows, carrying each item's crate and location with it so
    /// a list screen needs no follow-up lookups.
    /// </summary>
    /// <remarks>
    /// Columns rather than whole entities: an Item now carries a tsvector, and
    /// nobody wants a search document per row shipped to a phone. <paramref
    /// name="limit"/> is applied after the projection so the SQL stays one
    /// statement with a LIMIT, rather than a projection over a subquery whose
    /// ordering is then anyone's guess.
    /// </remarks>
    private static async Task<IReadOnlyList<ItemIndexRow>> IndexRowsAsync(
        IQueryable<Item> items, CancellationToken ct, int? limit = null)
    {
        var query = items.Select(i => new
        {
            i.Id,
            i.Name,
            i.Quantity,
            i.Notes,
            i.CrateId,
            CrateCode = i.Crate!.Code,
            CrateLabel = i.Crate!.Label,
            LocationId = i.Crate!.LocationId,
            LocationName = i.Crate!.Location == null ? null : i.Crate!.Location.Name,
        });

        if (limit is { } max) query = query.Take(max);

        var rows = await query.ToListAsync(ct);

        return
        [
            .. rows.Select(r => new ItemIndexRow(
                r.Id, r.Name, r.Quantity, r.Notes,
                r.CrateId, r.CrateCode, CrateCode.Format(r.CrateCode), r.CrateLabel,
                r.LocationId, r.LocationName))
        ];
    }

    [HttpGet("items/{id:guid}")]
    public async Task<ActionResult<ItemDto>> GetItem(Guid id, CancellationToken ct)
    {
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        return item is null ? NotFound() : ToDto(item);
    }

    [HttpPost("items")]
    public async Task<ActionResult<ItemDto>> CreateItem(ItemWriteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("name is required");
        if (request.Quantity < 1) return BadRequest("quantity must be at least 1");
        if (!await db.Crates.AnyAsync(c => c.Id == request.CrateId, ct)) return BadRequest("crateId does not exist");

        var now = time.GetUtcNow();
        var item = new Item
        {
            CrateId = request.CrateId,
            Name = request.Name.Trim(),
            Quantity = request.Quantity,
            Notes = request.Notes,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetItem), new { id = item.Id }, ToDto(item));
    }

    /// <summary>Also the "move this into another crate" path - CrateId is writable.</summary>
    [HttpPut("items/{id:guid}")]
    public async Task<ActionResult<ItemDto>> UpdateItem(Guid id, ItemWriteRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("name is required");
        if (request.Quantity < 1) return BadRequest("quantity must be at least 1");

        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return NotFound();

        if (item.CrateId != request.CrateId && !await db.Crates.AnyAsync(c => c.Id == request.CrateId, ct))
            return BadRequest("crateId does not exist");

        item.CrateId = request.CrateId;
        item.Name = request.Name.Trim();
        item.Quantity = request.Quantity;
        item.Notes = request.Notes;
        item.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    [HttpDelete("items/{id:guid}")]
    public async Task<IActionResult> DeleteItem(Guid id, CancellationToken ct)
    {
        var item = await db.Items.FindAsync([id], ct);
        if (item is null) return NotFound();

        db.Items.Remove(item);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static ItemDto ToDto(Item i) => new(i.Id, i.CrateId, i.Name, i.Quantity, i.Notes, i.CreatedAt, i.UpdatedAt);
}
