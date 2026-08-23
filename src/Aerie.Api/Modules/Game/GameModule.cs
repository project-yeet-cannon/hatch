namespace Aerie.Api.Modules.Game;

/// <summary>
/// Everything the game module puts into DI, so the registry in
/// ModuleRegistration stays one line per app.
/// </summary>
public static class GameModule
{
    public static IServiceCollection AddGameModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleContext<GameContext>(configuration, GameContext.Schema);
        services.AddScoped<IGameService, GameService>();

        // Singleton: it caches one Anthropic client per API key, and a client
        // per request would mean a connection pool per request.
        services.AddSingleton<IGameAuthor, GameAuthor>();
        return services;
    }
}
