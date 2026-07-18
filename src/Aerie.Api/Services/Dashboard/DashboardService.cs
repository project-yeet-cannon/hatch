using Aerie.Api.Models.Dashboard;
using Microsoft.Extensions.Options;

namespace Aerie.Api.Services.Dashboard;

public interface IDashboardService
{
    Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct);
}

/// <summary>
/// The backend-for-frontend aggregate: composes zones + outside into the exact
/// shape Aerie.Dashboard consumes. Zones and outside are fetched concurrently so
/// the (slower) weather call doesn't serialize behind the DB reads.
/// </summary>
public class DashboardService(
    IZoneService zones,
    IWeatherService weather,
    IOptions<DashboardOptions> options,
    TimeProvider time) : IDashboardService
{
    public async Task<DashboardData> GetDashboardAsync(DashboardWindow window, CancellationToken ct)
    {
        var zonesTask = zones.GetZonesAsync(window, ct);
        var outsideTask = weather.GetOutsideAsync(window, ct);
        await Task.WhenAll(zonesTask, outsideTask);

        return new DashboardData(
            GeneratedAt: time.GetUtcNow(),
            Timezone: options.Value.TimeZone,
            Zones: await zonesTask,
            Outside: await outsideTask);
    }
}
