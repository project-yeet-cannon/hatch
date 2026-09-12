using HADotNet.Core;

namespace Hatch.Api.Services.DeviceMapping;

/// <summary>
/// Ensures HADotNet's static ClientFactory is initialized before anything
/// resolves a client from it. ClientFactory.Initialize sets process-local
/// static state, not a database row - see ApplyAsync above - so unlike the
/// migration/seed work in Program.cs's migrate mode, it can't run once per
/// deploy in a separate Job process; it has to run once per api replica.
/// Every AddTransient registration for an HA client (Program.cs) routes
/// through EnsureInitialized first, so whichever replica and whichever
/// request or job first needs a client is the one that pays for the DB read
/// behind ApplyAsync; everyone after that sees ClientFactory.IsInitialized
/// and returns immediately.
/// </summary>
public class HomeAssistantClientFactoryGate(ILogger<HomeAssistantClientFactoryGate> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private bool loggedUnreadable;

    public void EnsureInitialized(IServiceProvider services)
    {
        if (ClientFactory.IsInitialized) return;

        Gate.Wait();
        try
        {
            if (ClientFactory.IsInitialized) return;
            services.GetRequiredService<IHomeAssistantConnectionManager>()
                .ApplyAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // This runs inside DI resolution, on the startup path, for every
            // HA client anything asks for - so an exception here is an
            // exception out of Main, and a database nobody has migrated yet
            // (SiteSettings does not exist) is enough to cause one. Failing to
            // *read* the settings is not the same as having no Home Assistant,
            // but it has the same answer: leave ClientFactory uninitialized
            // and carry on. The health check still reports the database
            // honestly, and SettingsController still surfaces a failure to the
            // person who caused it.
            //
            // Deliberately narrow: it covers this one call, and it says what
            // it decided. Logged once per process, the way
            // HomeAssistantEventListener.loggedUnconfigured does, because
            // otherwise every client resolution would repeat it.
            if (!loggedUnreadable)
            {
                logger.LogInformation(
                    ex,
                    "Could not read the site settings; treating Home Assistant as unconfigured for the life of this process.");
                loggedUnreadable = true;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
}
