using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aerie.Api.Modules.Storage;

/// <summary>Draws candidate crate codes. An interface only so tests can force a collision.</summary>
public interface ICrateCodeSource
{
    string Next();
}

public sealed class RandomCrateCodeSource : ICrateCodeSource
{
    public string Next() => CrateCode.Next();
}

public interface IStorageService
{
    /// <summary>The crate-creation path, which owns code generation. Count is caller-validated against <see cref="StorageService.MaxBatchCount"/>.</summary>
    Task<IReadOnlyList<Crate>> CreateCratesAsync(int count, CrateWriteRequest? initial, CancellationToken ct);
}

/// <summary>
/// The part of Storage Helper that isn't plain CRUD: minting crates with a
/// unique, human-typeable code. Everything else lives in StorageController
/// against the context directly, the same way ZonesController does.
/// </summary>
public class StorageService(StorageContext db, ICrateCodeSource codes, TimeProvider time, ILogger<StorageService> logger) : IStorageService
{
    /// <summary>
    /// One print run's worth of labels. A cap only so a typo in the batch box
    /// can't mint ten thousand crates; raise it if a sheet ever needs more.
    /// </summary>
    public const int MaxBatchCount = 200;

    /// <summary>Insert retries after another writer takes a code between the check and the insert.</summary>
    private const int MaxInsertAttempts = 3;

    /// <summary>Draws allowed per code requested, before giving up rather than spinning.</summary>
    private const int MaxDrawsPerCode = 10;

    public async Task<IReadOnlyList<Crate>> CreateCratesAsync(int count, CrateWriteRequest? initial, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxBatchCount);

        for (var attempt = 1; ; attempt++)
        {
            var now = time.GetUtcNow();
            var crates = (await DrawFreeCodesAsync(count, ct))
                .Select(code => new Crate
                {
                    Code = code,
                    Label = initial?.Label,
                    LocationId = initial?.LocationId,
                    Notes = initial?.Notes,
                    CreatedAt = now,
                    UpdatedAt = now,
                })
                .ToList();

            db.Crates.AddRange(crates);

            try
            {
                await db.SaveChangesAsync(ct);
                return crates;
            }
            catch (DbUpdateException dx) when (attempt < MaxInsertAttempts && IsCodeCollision(dx))
            {
                // The pre-insert check is not a lock: two batches drawn at the same
                // moment can pick the same code. Rare enough to just redraw the whole
                // batch, and cheap enough that doing so beats holding a transaction.
                logger.LogInformation("Crate code collided on insert (attempt {Attempt}), redrawing", attempt);
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// <paramref name="count"/> codes that no crate currently holds. Drawn in one
    /// batch and checked in one query, so minting a sheet of 100 labels is two
    /// round trips rather than 100.
    /// </summary>
    private async Task<List<string>> DrawFreeCodesAsync(int count, CancellationToken ct)
    {
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        var draws = 0;
        var maxDraws = count * MaxDrawsPerCode;

        while (drawn.Count < count)
        {
            while (drawn.Count < count)
            {
                if (draws++ >= maxDraws)
                {
                    // Unreachable against a real RNG at any household scale (32^6
                    // codes); a loud failure beats an infinite loop if it ever isn't.
                    throw new InvalidOperationException($"Could not draw {count} free crate codes in {maxDraws} attempts.");
                }

                // The set absorbs in-batch duplicates; the query below removes taken ones.
                drawn.Add(codes.Next());
            }

            var taken = await db.Crates.AsNoTracking()
                .Where(c => drawn.Contains(c.Code))
                .Select(c => c.Code)
                .ToListAsync(ct);

            drawn.ExceptWith(taken);
        }

        return [.. drawn];
    }

    private static bool IsCodeCollision(DbUpdateException dx) =>
        dx.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
