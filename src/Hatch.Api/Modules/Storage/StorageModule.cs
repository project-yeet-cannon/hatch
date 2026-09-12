namespace Hatch.Api.Modules.Storage;

/// <summary>
/// Everything Storage Helper puts into DI, so the module registry in
/// ModuleRegistration stays one line per app and a module's services live in
/// the module's folder.
/// </summary>
public static class StorageModule
{
    public static IServiceCollection AddStorageModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleContext<StorageContext>(configuration, StorageContext.Schema);
        services.AddSingleton<ICrateCodeSource, RandomCrateCodeSource>();
        services.AddScoped<IStorageService, StorageService>();
        return services;
    }
}
