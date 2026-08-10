using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aerie.Api.Modules;

/// <summary>
/// Wires module <c>DbContext</c>s into DI. Modules share the one Aerie database
/// (so cross-app queries stay plain SQL and one backup covers everything) but
/// each gets its own schema and its own migration history table.
/// </summary>
public static class ModuleRegistration
{
    /// <summary>
    /// The module registry. Program.cs calls this once; adding an app adds a line
    /// here and nothing anywhere else - see Modules/README.md.
    /// </summary>
    public static IServiceCollection AddAerieModules(this IServiceCollection services, IConfiguration configuration)
    {
        // One line per module, e.g.:
        //   services.AddModuleContext<StorageContext>(configuration, StorageContext.Schema);
        return services;
    }

    /// <summary>
    /// Registers a module context against the shared connection string, pinning its
    /// migration history table into <paramref name="schema"/> so <c>dotnet ef</c> and
    /// the startup migrator both track that module independently of everything else.
    /// </summary>
    public static IServiceCollection AddModuleContext<TContext>(
        this IServiceCollection services, IConfiguration configuration, string schema)
        where TContext : DbContext, IModuleContext
    {
        services.AddDbContext<TContext>(o =>
            ConfigureModule(o, configuration.GetConnectionString("Aerie"), schema));

        // Registered a second time under the marker interface: this is what lets the
        // startup migration loop find every module context without knowing its type.
        services.AddScoped<IModuleContext>(sp => sp.GetRequiredService<TContext>());

        return services;
    }

    /// <summary>
    /// The provider configuration a module context needs, shared by DI registration
    /// above and <see cref="ModuleDesignTimeFactory{TContext}"/>, so a scaffolded
    /// migration lands in the same history table the running app reads.
    /// </summary>
    public static void ConfigureModule(DbContextOptionsBuilder options, string? connectionString, string schema) =>
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema));
}
