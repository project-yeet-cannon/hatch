namespace Aerie.Api.Modules.Hatch;

/// <summary>
/// Everything Hatch puts into DI, so the module registry in ModuleRegistration
/// stays one line per app.
/// </summary>
public static class HatchModule
{
    public static IServiceCollection AddHatchModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleContext<HatchContext>(configuration, HatchContext.Schema);
        return services;
    }
}
