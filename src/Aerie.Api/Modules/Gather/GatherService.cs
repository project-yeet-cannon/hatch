using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aerie.Api.Modules.Gather;

public interface IGatherService
{
    /// <summary>
    /// Adds an item to a list, or brings back the one already there under the
    /// same normalized name. A re-add of a <em>checked</em> item un-checks it:
    /// someone typing "milk" onto a list wants milk, and the row that says it
    /// was already bought is the stale one.
    /// </summary>
    /// <remarks>
    /// Quantity and note are merged, not overwritten - a bare re-add of "milk"
    /// must not silently erase the "2 gal" someone else put on the row. Editing
    /// through <c>PUT items/{id}</c> is how you clear one.
    /// The list is assumed to exist; the caller has already 404'd otherwise.
    /// </remarks>
    Task<GatherItem> AddItemAsync(Guid listId, ItemAddRequest request, CancellationToken ct);

    /// <summary>
    /// Removes every checked item from a list and reports how many went, so the
    /// client can say what it just did rather than guessing from a re-fetch.
    /// </summary>
    Task<int> ClearCheckedAsync(Guid listId, CancellationToken ct);
}

/// <summary>
/// The two Gather operations with behaviour worth isolating: the add upsert and
/// the clear-checked sweep. Plain CRUD runs against GatherContext directly in
/// GatherController, the same way Storage does it.
/// </summary>
public class GatherService(GatherContext db, TimeProvider time, ILogger<GatherService> logger) : IGatherService
{
    /// <summary>Insert retries after another client adds the same name between the lookup and the insert.</summary>
    private const int MaxInsertAttempts = 3;

    public async Task<GatherItem> AddItemAsync(Guid listId, ItemAddRequest request, CancellationToken ct)
    {
        var normalized = GatherItem.Normalize(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalized);

        for (var attempt = 1; ; attempt++)
        {
            var now = time.GetUtcNow();

            var existing = await db.Items.FirstOrDefaultAsync(
                i => i.ListId == listId && i.NameNormalized == normalized, ct);

            if (existing is not null)
            {
                // The re-add. Name keeps the spelling already on the list: two
                // people writing the same thing differently is not a rename, and
                // the list shouldn't flicker between their capitalisations.
                existing.IsChecked = false;
                existing.CheckedAt = null;
                if (!string.IsNullOrWhiteSpace(request.Quantity)) existing.Quantity = request.Quantity.Trim();
                if (!string.IsNullOrWhiteSpace(request.Note)) existing.Note = request.Note.Trim();
                existing.UpdatedAt = now;
                await TouchListAsync(listId, now, ct);
                await db.SaveChangesAsync(ct);
                return existing;
            }

            var item = new GatherItem
            {
                ListId = listId,
                Name = request.Name,
                Quantity = Clean(request.Quantity),
                Note = Clean(request.Note),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Items.Add(item);
            await TouchListAsync(listId, now, ct);

            try
            {
                await db.SaveChangesAsync(ct);
                return item;
            }
            catch (DbUpdateException dx) when (attempt < MaxInsertAttempts && IsDuplicateName(dx))
            {
                // The lookup above is not a lock: the kiosk and a phone can add
                // "milk" in the same instant. The unique index turns that into
                // this retry - which finds the row the other writer inserted and
                // takes the re-add path - rather than a second milk.
                logger.LogInformation(
                    "Item name collided on insert into list {ListId} (attempt {Attempt}), re-reading", listId, attempt);
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<int> ClearCheckedAsync(Guid listId, CancellationToken ct)
    {
        var checkedItems = await db.Items.Where(i => i.ListId == listId && i.IsChecked).ToListAsync(ct);
        if (checkedItems.Count == 0) return 0;

        db.Items.RemoveRange(checkedItems);
        await TouchListAsync(listId, time.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
        return checkedItems.Count;
    }

    /// <summary>
    /// Display order, server-side and total: unchecked first oldest-first (the
    /// order they were thought of, which is the order the aisle gets walked),
    /// then checked most-recently-checked first, so the last thing crossed off
    /// is the one that's easy to un-cross. Id breaks any remaining tie so a poll
    /// never reshuffles rows under a thumb.
    /// </summary>
    /// <remarks>
    /// One implementation, used by every read path, because two clients each
    /// sorting for themselves is two chances to disagree about what the list
    /// looks like. CheckedAt is null across the whole unchecked block, so it
    /// ties there and CreatedAt does the work.
    /// </remarks>
    public static IOrderedQueryable<GatherItem> InDisplayOrder(IQueryable<GatherItem> items) =>
        items.OrderBy(i => i.IsChecked)
            .ThenByDescending(i => i.CheckedAt)
            .ThenBy(i => i.CreatedAt)
            .ThenBy(i => i.Id);

    /// <summary>
    /// Whether a failed save was the <c>(ListId, NameNormalized)</c> index
    /// refusing a duplicate - the one <c>DbUpdateException</c> Gather expects
    /// and handles, here and on rename in the controller.
    /// </summary>
    public static bool IsDuplicateName(DbUpdateException dx) =>
        dx.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Marks the list itself as touched, so "last activity" on a lists screen
    /// means what it says. Loaded rather than ExecuteUpdate'd so it rides the
    /// same SaveChanges as the item write - and because ExecuteUpdate has no
    /// InMemory implementation, which is what the module's tests run on.
    /// </summary>
    private async Task TouchListAsync(Guid listId, DateTimeOffset now, CancellationToken ct)
    {
        var list = await db.Lists.FirstOrDefaultAsync(l => l.Id == listId, ct);
        if (list is not null) list.UpdatedAt = now;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
