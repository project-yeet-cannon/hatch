using Hatch.Api.Ef;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hatch.Api.Common;

// Backs /health/ready (see Program.cs), tagged "ready" so /health/live -
// which maps no checks at all - stays a pure process-is-up probe. A pod that
// can't reach the database has to leave the Service via readiness rather
// than restart via liveness: restarting every replica at once over a
// database blip just adds a thundering herd to whatever caused the blip.
public class DatabaseHealthCheck(IDbContextFactory<AppDbContext> dbFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database not reachable");
    }
}
