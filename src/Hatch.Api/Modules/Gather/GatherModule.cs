namespace Hatch.Api.Modules.Gather;

/// <summary>
/// Everything Gather puts into DI, so the module registry in
/// ModuleRegistration stays one line per app and a module's services live in
/// the module's folder.
/// </summary>
public static class GatherModule
{
    public static IServiceCollection AddGatherModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleContext<GatherContext>(configuration, GatherContext.Schema);
        services.AddScoped<IGatherService, GatherService>();
        return services;
    }
}
