using Hatch.Api.Ef;
using Hatch.Api.Models.Dashboard;
using Hatch.Api.Services.DeviceMapping;
using Microsoft.EntityFrameworkCore;

namespace Hatch.Api.Services.Routines;

public interface IRoutineService
{
    Task<IReadOnlyList<RoutineSummary>> GetRoutinesAsync(CancellationToken ct);
}

/// <summary>The dashboard's read path for Routines - mirrors ZoneService.GetZonesAsync's Included/SortOrder filtering.</summary>
public class RoutineService(IDbContextFactory<AppDbContext> dbFactory) : IRoutineService
{
    public async Task<IReadOnlyList<RoutineSummary>> GetRoutinesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var routines = await db.Routines.AsNoTracking()
            .Where(r => r.Included)
            .Include(r => r.Actions)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

        // Only toggle routines need live channel state, so scope the (per-channel)
        // ChannelLatestValues lookup to just their SetPower actions rather than
        // paying it for every routine's channels.
        var powerChannelIds = routines
            .Where(r => r.IsToggle)
            .SelectMany(RoutineToggleState.PowerChannelIds)
            .Distinct()
            .ToList();
        var latest = await ChannelLatestValues.GetLatestAsync(db, powerChannelIds, ct);

        return routines
            .Select(r => new RoutineSummary(r.Id, r.Name, r.Description, r.Icon, r.Color, r.IsToggle, IsActive: RoutineToggleState.IsActive(r, latest)))
            .ToList();
    }
}
