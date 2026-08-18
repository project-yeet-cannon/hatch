using HADotNet.Core;

namespace Aerie.Api.Services.DeviceMapping;

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
public class HomeAssistantClientFactoryGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

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
        finally
        {
            Gate.Release();
        }
    }
}
