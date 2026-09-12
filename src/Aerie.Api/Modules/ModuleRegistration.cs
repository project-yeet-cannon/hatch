using Aerie.Api.Modules.Gather;
using Aerie.Api.Modules.Hatch;
using Aerie.Api.Modules.Photos;
using Aerie.Api.Modules.Quill;
using Aerie.Api.Modules.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        // Platform config every app in the family shell can read - see AppsController.
        services.Configure<AppsOptions>(configuration.GetSection(AppsOptions.SectionName));

        // One line per module - each module's own extension registers its context
        // and its services, so this list stays a table of contents.
        services.AddStorageModule(configuration);
        services.AddGatherModule(configuration);
        services.AddPhotosModule(configuration);
        services.AddQuillModule(configuration);
        services.AddHatchModule(configuration);

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
        // Same reasoning as Program.cs's AerieContext registration: in migrate
        // mode the first command against an empty database is a probe of a
        // history table that does not exist yet, and one Error line per module
        // context reads as a broken install. Everywhere else a failed command
        // stays news.
        var quietCommandErrors = configuration["AERIE_MIGRATE"] == "1";

        services.AddDbContext<TContext>(o =>
            ConfigureModule(o, configuration.GetConnectionString("Aerie"), schema, quietCommandErrors));

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
    /// <param name="quietCommandErrors">
    /// Downgrades EF's Error-level CommandError to Debug. Optional and off by
    /// default so <see cref="ModuleDesignTimeFactory{TContext}"/>, which has no
    /// configuration to read the flag from, keeps its one call site untouched.
    /// </param>
    public static void ConfigureModule(
        DbContextOptionsBuilder options, string? connectionString, string schema, bool quietCommandErrors = false)
    {
        options.UseNpgsql(connectionString, npgsql =>
            npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema));

        if (quietCommandErrors)
            options.ConfigureWarnings(w => w.Log((RelationalEventId.CommandError, LogLevel.Debug)));
    }
}
