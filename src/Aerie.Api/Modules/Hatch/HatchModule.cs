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

        // Scoped, because it reads and rewrites rows through the request's own
        // context - a renumbered column and the card that caused it have to
        // land in one SaveChanges.
        services.AddScoped<RankService>();

        // Singleton, because it is a pure function with a class around it: it
        // reads a string and returns a tree, and touches neither the database
        // nor the clock.
        services.AddSingleton<PlanImportParser>();

        // The battery in the nav, in three registrations. All singletons, and
        // the chain has to be: UtilizationCache holds the last good reading for
        // the whole process - which is what lets an unreachable account still
        // answer with something - and a singleton may not depend on anything
        // shorter-lived than itself.
        //
        // Nothing here wants a scope anyway. The credential is its own
        // interface so that where the token lives is one class rather than a
        // decision spread through the adapter, and it reads through
        // ISiteSettingsService, which is a singleton with its own cache; the
        // adapter takes IHttpClientFactory, which is the whole reason a named
        // client exists.
        services.AddSingleton<IClaudeCredential, SiteSettingClaudeCredential>();

        services.AddHttpClient(ClaudeUsageClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IClaudeUsageClient, ClaudeUsageClient>();

        services.AddSingleton<UtilizationCache>();

        return services;
    }
}
