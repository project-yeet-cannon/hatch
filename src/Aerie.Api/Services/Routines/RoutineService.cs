using Aerie.Api.Ef;
using Aerie.Api.Models.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Services.Routines;

public interface IRoutineService
{
    Task<IReadOnlyList<RoutineSummary>> GetRoutinesAsync(CancellationToken ct);
}

/// <summary>The dashboard's read path for Routines - mirrors ZoneService.GetZonesAsync's Included/SortOrder filtering.</summary>
public class RoutineService(IDbContextFactory<AerieContext> dbFactory) : IRoutineService
{
    public async Task<IReadOnlyList<RoutineSummary>> GetRoutinesAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Routines.AsNoTracking()
            .Where(r => r.Included)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name)
            .Select(r => new RoutineSummary(r.Id, r.Name, r.Description, r.Icon, r.Color))
            .ToListAsync(ct);
    }
}
