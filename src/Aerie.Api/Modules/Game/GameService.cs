using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Modules.Game;

public interface IGameService
{
    Task<IReadOnlyList<WorldSummaryDto>> GetWorldsAsync(CancellationToken ct);

    Task<WorldDto?> GetWorldAsync(Guid worldId, CancellationToken ct);

    /// <summary>
    /// The world the play screen opens on: the most recently touched one, or a
    /// brand new blank one if the house has never played before. Opening the app
    /// is meant to land on a game, not on a list with nothing in it.
    /// </summary>
    Task<WorldDto> GetOrCreateCurrentWorldAsync(CancellationToken ct);

    Task<WorldDto> CreateWorldAsync(WorldWriteRequest request, CancellationToken ct);
    Task<WorldSummaryDto?> RenameWorldAsync(Guid worldId, WorldWriteRequest request, CancellationToken ct);
    Task<bool> DeleteWorldAsync(Guid worldId, CancellationToken ct);

    Task<IReadOnlyList<VersionDto>> GetVersionsAsync(Guid worldId, CancellationToken ct);

    Task<WorldDto?> TakeTurnAsync(Guid worldId, TurnRequest request, CancellationToken ct);
    Task<WorldDto?> RepairAsync(Guid worldId, BreakageReport report, CancellationToken ct);
    Task<WorldDto?> RevertAsync(Guid worldId, Guid versionId, CancellationToken ct);

    /// <summary>Back to the newest version older than the current one that still works.</summary>
    Task<WorldDto?> UndoAsync(Guid worldId, CancellationToken ct);

    /// <summary>Records the frame's crash report against a version, so nothing goes back to it.</summary>
    Task<bool> ReportBreakageAsync(Guid worldId, BreakageReport report, CancellationToken ct);

    Task<bool> SaveRecordsAsync(Guid worldId, RecordsWriteRequest request, CancellationToken ct);
}

/// <summary>
/// Worlds, versions, and the turn loop between them.
/// </summary>
/// <remarks>
/// Everything here is arranged around one asymmetry: a turn that improves the
/// game is worth a wait, and a turn that breaks it is an emergency, because the
/// person holding the tablet cannot read the error, cannot describe it, and
/// will not wait. So a version that throws is marked and never returned to,
/// undo is a single call with no arguments, and every path that could hand back
/// broken code has a good version behind it to fall back to.
/// </remarks>
public class GameService(GameContext db, IGameAuthor author, TimeProvider time) : IGameService
{
    /// <summary>How many earlier requests ride along as continuity - enough to remember the shape of the afternoon, not enough to pay for it.</summary>
    private const int PromptHistoryDepth = 8;

    /// <summary>
    /// What a new world runs before anyone has asked for anything: a sky and
    /// two clouds. Not literally nothing, because a blank black rectangle reads
    /// as a broken app, and this reads as an empty one waiting to be told.
    /// </summary>
    public const string SeedCode = """
        defineGame({
          setup(w) {
            w.sky('#8fd3f4', '#e8f7ff');
            w.gravity(0);
            w.emoji({ x: -160, y: -60, char: '☁️', size: 64, ghost: true, vx: 14 });
            w.emoji({ x: 220, y: 40, char: '☁️', size: 92, ghost: true, vx: 9 });
          },

          update(w) {
            // Drift forever: clouds that leave on the right come back on the left.
            for (const body of w.bodies) {
              if (body.x > w.width) body.x = -w.width;
            }
          },
        });
        """;

    public async Task<IReadOnlyList<WorldSummaryDto>> GetWorldsAsync(CancellationToken ct)
    {
        var worlds = await db.Worlds
            .AsNoTracking()
            .Include(w => w.Versions)
            .OrderByDescending(w => w.UpdatedAt)
            .ToListAsync(ct);

        return worlds.Select(ToSummary).ToList();
    }

    public async Task<WorldDto?> GetWorldAsync(Guid worldId, CancellationToken ct)
    {
        var world = await LoadAsync(worldId, ct);
        return world is null ? null : ToDto(world);
    }

    public async Task<WorldDto> GetOrCreateCurrentWorldAsync(CancellationToken ct)
    {
        var newest = await db.Worlds
            .Include(w => w.Versions)
            .OrderByDescending(w => w.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        return newest is null
            ? await CreateWorldAsync(new WorldWriteRequest("Our game", "\U0001F3AE"), ct)
            : ToDto(newest);
    }

    public async Task<WorldDto> CreateWorldAsync(WorldWriteRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var world = new GameWorld
        {
            Name = Clamp(request.Name, GameWorld.MaxNameLength, "Our game"),
            Icon = string.IsNullOrWhiteSpace(request.Icon) ? "\U0001F3AE" : Clamp(request.Icon, GameWorld.MaxIconLength, "\U0001F3AE"),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var seed = new GameVersion
        {
            World = world,
            Ordinal = 1,
            Kind = GameVersionKind.Seed,
            Code = SeedCode,
            Summary = "A brand new sky. Tell it what to make!",
            CreatedAt = now,
        };

        world.Versions.Add(seed);
        db.Worlds.Add(world);
        await db.SaveChangesAsync(ct);

        // The seed's id only exists after the insert, so the pointer is a
        // second write rather than something the object graph could carry.
        world.CurrentVersionId = seed.Id;
        await db.SaveChangesAsync(ct);

        return ToDto(world);
    }

    public async Task<WorldSummaryDto?> RenameWorldAsync(Guid worldId, WorldWriteRequest request, CancellationToken ct)
    {
        var world = await db.Worlds.Include(w => w.Versions).FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return null;

        world.Name = Clamp(request.Name, GameWorld.MaxNameLength, world.Name);
        if (!string.IsNullOrWhiteSpace(request.Icon)) world.Icon = Clamp(request.Icon, GameWorld.MaxIconLength, world.Icon ?? "");
        world.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return ToSummary(world);
    }

    public async Task<bool> DeleteWorldAsync(Guid worldId, CancellationToken ct)
    {
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return false;

        db.Worlds.Remove(world);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<VersionDto>> GetVersionsAsync(Guid worldId, CancellationToken ct)
    {
        var versions = await db.Versions
            .AsNoTracking()
            .Where(v => v.WorldId == worldId)
            .OrderByDescending(v => v.Ordinal)
            .ToListAsync(ct);

        return versions.Select(ToDto).ToList();
    }

    public async Task<WorldDto?> TakeTurnAsync(Guid worldId, TurnRequest request, CancellationToken ct)
    {
        var world = await LoadAsync(worldId, ct);
        if (world is null) return null;

        var prompt = Clamp(request.Prompt, GameVersion.MaxPromptLength, "");
        if (prompt.Length == 0) throw new GameAuthorException("Type what you want first.");

        // Build on the last version known to work, not simply the current one.
        // They are the same thing except right after a crash, and that is
        // exactly when handing the model code that throws would turn one bad
        // turn into every following turn.
        var basis = LastGoodVersion(world);

        var authored = await author.WriteAsync(
            new GameAuthorRequest(
                CurrentCode: basis?.Kind == GameVersionKind.Seed ? null : basis?.Code,
                RecentPrompts: RecentPrompts(world),
                Instruction: prompt,
                IsRepair: false,
                Model: request.Model ?? GameModelChoice.Quick),
            ct);

        Append(world, authored, GameVersionKind.Turn, prompt, basis?.Id);
        await db.SaveChangesAsync(ct);
        return ToDto(world);
    }

    public async Task<WorldDto?> RepairAsync(Guid worldId, BreakageReport report, CancellationToken ct)
    {
        var world = await LoadAsync(worldId, ct);
        if (world is null) return null;

        var broken = world.Versions.FirstOrDefault(v => v.Id == report.VersionId);
        // A frame left open in another tab can report a version that is no
        // longer live. Repairing on its word would overwrite whatever the
        // person actually playing is looking at.
        if (broken is null || world.CurrentVersionId != broken.Id) return ToDto(world);

        MarkBroken(broken, report);

        var authored = await author.WriteAsync(
            new GameAuthorRequest(
                CurrentCode: broken.Code,
                RecentPrompts: RecentPrompts(world),
                Instruction: DescribeBreakage(report),
                IsRepair: true,
                // A repair is always quick: the child is already staring at a
                // stopped game, and the fix is nearly always small.
                Model: GameModelChoice.Quick),
            ct);

        Append(world, authored, GameVersionKind.Repair, broken.Prompt, broken.Id);
        await db.SaveChangesAsync(ct);
        return ToDto(world);
    }

    public async Task<WorldDto?> RevertAsync(Guid worldId, Guid versionId, CancellationToken ct)
    {
        var world = await LoadAsync(worldId, ct);
        if (world is null) return null;

        var target = world.Versions.FirstOrDefault(v => v.Id == versionId);
        if (target is null) throw new GameAuthorException("That version is gone.");
        if (target.BrokenAt is not null) throw new GameAuthorException("That one was broken. Pick another.");

        Restore(world, target);
        await db.SaveChangesAsync(ct);
        return ToDto(world);
    }

    public async Task<WorldDto?> UndoAsync(Guid worldId, CancellationToken ct)
    {
        var world = await LoadAsync(worldId, ct);
        if (world is null) return null;

        var current = Current(world);
        var target = world.Versions
            .Where(v => v.BrokenAt is null && (current is null || v.Ordinal < current.Ordinal))
            .OrderByDescending(v => v.Ordinal)
            .FirstOrDefault();

        if (target is null) throw new GameAuthorException("This is where the game started - there is nothing before it.");

        Restore(world, target);
        await db.SaveChangesAsync(ct);
        return ToDto(world);
    }

    public async Task<bool> ReportBreakageAsync(Guid worldId, BreakageReport report, CancellationToken ct)
    {
        var version = await db.Versions.FirstOrDefaultAsync(v => v.Id == report.VersionId && v.WorldId == worldId, ct);
        if (version is null) return false;

        MarkBroken(version, report);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SaveRecordsAsync(Guid worldId, RecordsWriteRequest request, CancellationToken ct)
    {
        var world = await db.Worlds.FirstOrDefaultAsync(w => w.Id == worldId, ct);
        if (world is null) return false;

        world.RecordsJson = JsonSerializer.Serialize(request.Records);
        world.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ------------------------------------------------------------------ internals

    private Task<GameWorld?> LoadAsync(Guid worldId, CancellationToken ct) =>
        db.Worlds.Include(w => w.Versions).FirstOrDefaultAsync(w => w.Id == worldId, ct);

    private static GameVersion? Current(GameWorld world) =>
        world.Versions.FirstOrDefault(v => v.Id == world.CurrentVersionId)
        ?? world.Versions.OrderByDescending(v => v.Ordinal).FirstOrDefault();

    private static GameVersion? LastGoodVersion(GameWorld world)
    {
        var current = Current(world);
        if (current is { BrokenAt: null }) return current;

        return world.Versions
            .Where(v => v.BrokenAt is null)
            .OrderByDescending(v => v.Ordinal)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> RecentPrompts(GameWorld world) =>
        world.Versions
            .Where(v => v.Kind == GameVersionKind.Turn && !string.IsNullOrWhiteSpace(v.Prompt))
            .OrderByDescending(v => v.Ordinal)
            .Take(PromptHistoryDepth)
            .Reverse()
            .Select(v => v.Prompt!)
            .ToList();

    /// <summary>Adds a written version and makes it live.</summary>
    private void Append(GameWorld world, AuthoredGame authored, GameVersionKind kind, string? prompt, Guid? parentId)
    {
        var now = time.GetUtcNow();
        var version = new GameVersion
        {
            WorldId = world.Id,
            Ordinal = NextOrdinal(world),
            Kind = kind,
            Prompt = prompt,
            Code = authored.Code,
            Summary = Clamp(authored.Summary, GameVersion.MaxSummaryLength, ""),
            Extra = authored.Extra is null ? null : Clamp(authored.Extra, GameVersion.MaxSummaryLength, ""),
            Model = Clamp(authored.Model, GameVersion.MaxModelLength, ""),
            ParentVersionId = parentId,
            CreatedAt = now,
            DurationMs = authored.DurationMs,
            InputTokens = authored.InputTokens,
            OutputTokens = authored.OutputTokens,
            CachedInputTokens = authored.CachedInputTokens,
        };

        world.Versions.Add(version);
        db.Versions.Add(version);
        world.CurrentVersionId = version.Id;
        world.UpdatedAt = now;
    }

    /// <summary>
    /// Going back is a new version holding the old code, not a moved pointer.
    /// It costs a row and buys two things: undoing an undo is just another
    /// step, and the history stays a record of what was played rather than a
    /// tree of what might have been.
    /// </summary>
    private void Restore(GameWorld world, GameVersion target)
    {
        var now = time.GetUtcNow();
        var version = new GameVersion
        {
            WorldId = world.Id,
            Ordinal = NextOrdinal(world),
            Kind = GameVersionKind.Revert,
            Prompt = target.Prompt,
            Code = target.Code,
            Summary = target.Summary is null ? "Back to how it was." : $"Back to: {target.Summary}",
            Model = target.Model,
            ParentVersionId = target.Id,
            CreatedAt = now,
        };

        world.Versions.Add(version);
        db.Versions.Add(version);
        world.CurrentVersionId = version.Id;
        world.UpdatedAt = now;
    }

    private static int NextOrdinal(GameWorld world) =>
        world.Versions.Count == 0 ? 1 : world.Versions.Max(v => v.Ordinal) + 1;

    private void MarkBroken(GameVersion version, BreakageReport report)
    {
        // First report wins: the frame stops its loop after one failure, but a
        // reload can replay the same crash, and the first one is the one that
        // happened closest to the turn that caused it.
        if (version.BrokenAt is not null) return;

        version.BrokenAt = time.GetUtcNow();
        version.BrokenError = Clamp(DescribeBreakage(report), GameVersion.MaxErrorLength, "");
    }

    private static string DescribeBreakage(BreakageReport report)
    {
        var stack = string.IsNullOrWhiteSpace(report.Stack) ? "" : $"\n{report.Stack.Trim()}";
        return $"[{report.Phase}] {report.Message.Trim()}{stack}";
    }

    private static string Clamp(string? value, int max, string fallback)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return fallback;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    // ------------------------------------------------------------------ mapping

    private static WorldSummaryDto ToSummary(GameWorld world) => new(
        world.Id,
        world.Name,
        world.Icon,
        world.Versions.Count,
        world.Versions
            .Where(v => v.Kind == GameVersionKind.Turn)
            .OrderByDescending(v => v.Ordinal)
            .Select(v => v.Prompt)
            .FirstOrDefault(),
        world.CreatedAt,
        world.UpdatedAt);

    private static WorldDto ToDto(GameWorld world)
    {
        var current = Current(world);
        return new WorldDto(
            ToSummary(world),
            current is null ? null : ToDto(current),
            current?.Code ?? SeedCode,
            ReadRecords(world.RecordsJson));
    }

    private static VersionDto ToDto(GameVersion version) => new(
        version.Id, version.Ordinal, version.Kind, version.Prompt, version.Summary, version.Extra,
        version.Model, version.CreatedAt, version.DurationMs, version.InputTokens, version.OutputTokens,
        version.CachedInputTokens, version.BrokenAt is not null);

    /// <summary>
    /// Records are stored as the JSON they arrived as. Anything unreadable -
    /// hand-edited, or written by an older shape of the engine - degrades to
    /// "no records yet" rather than taking the whole world down with it: a lost
    /// personal best is a shame, and an unopenable game is the end of the
    /// afternoon.
    /// </summary>
    private static IReadOnlyList<GameRecordDto> ReadRecords(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<GameRecordDto>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
