using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hatch.Api.Modules;

/// <summary>
/// Base for a module's design-time factory, so <c>dotnet ef</c> can build the
/// context without executing Program.cs (which reads .env.json and reaches out to
/// Home Assistant and Quartz at startup). Mirrors
/// <see cref="Ef.DesignTimeDbContextFactory"/> for the home-automation context.
/// </summary>
/// <remarks>
/// The EF CLI only discovers concrete, non-generic implementations of
/// <c>IDesignTimeDbContextFactory&lt;T&gt;</c>, so a module still needs its own
/// three-line subclass - this class is what keeps it to three lines.
/// </remarks>
public abstract class ModuleDesignTimeFactory<TContext> : IDesignTimeDbContextFactory<TContext>
    where TContext : DbContext, IModuleContext
{
    /// <summary>
    /// Must match the schema the module registers with
    /// <see cref="ModuleRegistration.AddModuleContext{TContext}"/> and the one its
    /// <c>OnModelCreating</c> sets via <c>HasDefaultSchema</c>. Point all three at a
    /// single <c>const</c> on the context.
    /// </summary>
    protected abstract string Schema { get; }

    /// <summary>
    /// Nominal for scaffolding (only the model shape matters), and the local dev
    /// database for <c>dotnet ef database update</c> - same value and same reasoning
    /// as the home-automation factory.
    /// </summary>
    private const string DesignTimeConnectionString = "Host=localhost;Database=hatch;Username=user;Password=password";

    public TContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TContext>();
        ModuleRegistration.ConfigureModule(options, DesignTimeConnectionString, Schema);
        return (TContext)Activator.CreateInstance(typeof(TContext), options.Options)!;
    }
}
