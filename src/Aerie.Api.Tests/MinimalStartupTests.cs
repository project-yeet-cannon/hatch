using Aerie.Api.Tests.Hatch;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Collections.Concurrent;
using System.Net;
using QuartzLogProvider = Quartz.Logging.LogProvider;

namespace Aerie.Api.Tests;

/// <summary>
/// The story's first criterion, asserted rather than remembered: the API,
/// given a database connection and nothing else, comes up.
///
/// <para>Nothing here is mocked. It boots the real host off the real
/// Program.cs against a real Postgres, supplying exactly the three settings a
/// friend's <c>docker run</c> supplies - the two connection strings and
/// <c>Auth:Enabled=false</c> - and no Home Assistant, no go2rtc, no share, no
/// kiosk host and no log-shipper token. That is the point: the failure it
/// guards against was a database read from inside DI resolution, which no
/// unit test of any one class can see.</para>
///
/// <para>It runs twice over the same pair of scratch databases: once against
/// one nobody has migrated, which is what used to exit 139 before the first
/// request, and once after MigrateAsync. Both have to start.</para>
///
/// <para>Skippable on the same variable as <see cref="HatchDatabase"/>, and
/// for the same reason - asserted in CI, where ci.yml's api job sets it, and
/// skipped out loud on a laptop with no Postgres rather than reported as
/// passed.</para>
/// </summary>
public class MinimalStartupTests
{
    /// <summary>Everything the four house jobs and BackfillChannelHistory are registered under - criterion 6 is that no trigger for it exists.</summary>
    private const string JobGroup = "Aerie.Api";

    /// <summary>
    /// The exit 139. Resolving JobsInit constructs every job, one of which takes
    /// an HADotNet client whose registration blocks on a SiteSettings read - and
    /// on a database the migrate step has not touched, that table does not
    /// exist. The exception used to unwind out of Main before the first request.
    ///
    /// <para>What is asserted is criterion 1's second half exactly: it stays up,
    /// it serves, and it says what it decided. Not the absence of Error lines -
    /// EF reports the failed read at Error, correctly, because a serving process
    /// that cannot run a command has something wrong with it. Something is: the
    /// migrate step has not run. Criterion 5's clean log is the case below,
    /// which is the one a friend is actually left in.</para>
    /// </summary>
    [SkippableFact]
    public async Task AgainstADatabaseNobodyHasMigrated_ItStaysUpAndSaysWhy()
    {
        Skip.IfNot(HatchDatabase.Available, $"{HatchDatabase.Variable} is unset");

        var (aerie, quartz) = await MinimalDatabases.CreateAsync();

        var logs = await AssertStartsAndServesAsync(aerie, quartz);

        Assert.Contains(logs.Messages, m => m.Contains("Could not read the site settings", StringComparison.Ordinal));
    }

    /// <summary>
    /// The install a friend is left with once the migrate step has run:
    /// criteria 1, 5 and 6 at startup. Nothing at Error or above, and not one
    /// of the four house jobs holding a trigger - which is what says the
    /// weather call that would have gone out to the internet, and the 7,200 log
    /// lines a day, are not going to happen.
    /// </summary>
    [SkippableFact]
    public async Task AgainstAMigratedDatabase_ItStartsCleanlyAndSchedulesNoHouseJob()
    {
        Skip.IfNot(HatchDatabase.Available, $"{HatchDatabase.Variable} is unset");

        var (aerie, quartz) = await MinimalDatabases.CreateAsync();
        await MinimalDatabases.MigrateAsync(aerie);

        var logs = await AssertStartsAndServesAsync(aerie, quartz);

        Assert.Empty(logs.AtErrorOrAbove);
    }

    /// <summary>
    /// What both cases share: the host builds and starts, /health/ready answers
    /// 200, and the store holds no trigger for any house job.
    /// </summary>
    private static async Task<CapturedLogs> AssertStartsAndServesAsync(string aerie, string quartz)
    {
        var logs = new CapturedLogs();

        // Quartz keeps its logging provider in a static, bound to whichever
        // host built a scheduler first. Production has one host per process and
        // never notices; a test file with two of them gets the *first* host's
        // LoggerFactory, disposed with it, and the second host dies resolving
        // IScheduler. Clearing it lets this host's AddQuartz rebind to its own.
        QuartzLogProvider.SetCurrentLogProvider(null);

        await using (var factory = new MinimalFactory(aerie, quartz, logs))
        {
            // Creating the client is what builds and starts the host - before
            // this line nothing has run, and the exit 139 came out of exactly
            // here.
            using var client = factory.CreateClient();

            var health = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        Assert.Empty(await MinimalDatabases.TriggerNamesAsync(quartz, JobGroup));

        return logs;
    }

    /// <summary>
    /// The three settings and nothing else, plus somewhere to log. The content
    /// root is a scratch directory rather than the Aerie.Api project's: a
    /// developer's own <c>.env.json</c> sits in the latter, and a test whose
    /// answer depends on what is in it is not asserting "nothing else is
    /// configured".
    /// </summary>
    private sealed class MinimalFactory(string aerie, string quartz, CapturedLogs logs) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(MinimalDatabases.ScratchContentRoot());

            builder.UseSetting("ConnectionStrings:Aerie", aerie);
            builder.UseSetting("ConnectionStrings:Quartz", quartz);
            builder.UseSetting("Auth:Enabled", "false");

            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }

    /// <summary>Everything the host said, so the test can assert on the absence of a level rather than on the presence of a string.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Category, string Message)> lines = new();

        public IReadOnlyCollection<string> Messages => [.. lines.Select(l => l.Message)];

        public IReadOnlyCollection<string> AtErrorOrAbove =>
            [.. lines.Where(l => l.Level >= LogLevel.Error).Select(l => $"[{l.Level}] {l.Category}: {l.Message}")];

        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, lines);

        public void Dispose() { }

        private sealed class Recorder(string category, ConcurrentQueue<(LogLevel, string, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue((logLevel, category, $"{formatter(state, exception)} {exception}"));
        }
    }
}

/// <summary>
/// A scratch <c>aerie</c> and a scratch <c>quartz</c>, made the way a friend's
/// machine makes them: two empty databases, with the Quartz DDL applied to the
/// second. Both are dropped and recreated per call, so each test gets the
/// blank slate its criterion is about.
/// </summary>
internal static class MinimalDatabases
{
    private static readonly SemaphoreSlim Once = new(1, 1);

    private static int counter;

    public static async Task<(string Aerie, string Quartz)> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable(HatchDatabase.Variable)
            ?? throw new InvalidOperationException($"{HatchDatabase.Variable} is unset");

        var template = new NpgsqlConnectionStringBuilder(HatchDatabase.ToConnectionString(configured.Trim()));

        // Suffixed off the database the variable names, so this can only ever
        // drop something it made - the variable's own database is never touched.
        var n = Interlocked.Increment(ref counter);
        var aerie = Named(template, $"{template.Database}_min{n}_aerie");
        var quartz = Named(template, $"{template.Database}_min{n}_quartz");

        // CREATE DATABASE cannot run inside the database being created, and
        // two of these racing on one server is a duplicate-name error rather
        // than a queue, so the maintenance work is serialized.
        await Once.WaitAsync();
        try
        {
            await using var maintenance = new NpgsqlConnection(Named(template, "postgres"));
            await maintenance.OpenAsync();

            foreach (var name in new[] { new NpgsqlConnectionStringBuilder(aerie).Database!, new NpgsqlConnectionStringBuilder(quartz).Database! })
            {
                // WITH (FORCE) rather than a plain DROP: Npgsql pools
                // connections across tests in this process, and a leftover idle
                // one is enough to make "database is being accessed by other
                // users" the result instead.
                await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS {Quote(name)} WITH (FORCE)");
                await ExecuteAsync(maintenance, $"CREATE DATABASE {Quote(name)}");
            }
        }
        finally
        {
            Once.Release();
        }

        // The one that ships, read from where the cluster reads it. Copying it
        // in here would be a fourth copy of the same DDL, and the copy the
        // tests read is the one that could silently stop matching the one the
        // cluster runs.
        await using (var quartzDb = new NpgsqlConnection(quartz))
        {
            await quartzDb.OpenAsync();
            await ExecuteAsync(quartzDb, await File.ReadAllTextAsync(
                Path.Combine(RepositoryRoot(), "deploy", "cluster", "data", "schema", "quartz-ddl.sql")));
        }

        return (aerie, quartz);
    }

    /// <summary>What AERIE_MIGRATE=1 does to the core schema, for the case that is about a database somebody has migrated.</summary>
    public static async Task MigrateAsync(string aerie)
    {
        await using var db = new Api.Ef.AerieContext(
            new DbContextOptionsBuilder<Api.Ef.AerieContext>().UseNpgsql(aerie).Options);
        await db.Database.MigrateAsync();
    }

    /// <summary>Criterion 6, asked of the store rather than of the log: which of this group's jobs hold a trigger.</summary>
    public static async Task<IReadOnlyList<string>> TriggerNamesAsync(string quartz, string group)
    {
        await using var db = new NpgsqlConnection(quartz);
        await db.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT trigger_name FROM qrtz_triggers WHERE trigger_group = @g", db);
        command.Parameters.AddWithValue("g", group);

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));

        return names;
    }

    /// <summary>
    /// A content root with a <c>wwwroot</c> in it and nothing else. The
    /// directory has to exist (Program.cs combines a path off WebRootPath) and
    /// has to be empty of apps, which is what makes the boot under test the one
    /// a friend gets rather than one shaped by whatever is in the working copy.
    /// </summary>
    public static string ScratchContentRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aerie-minimal-startup");
        Directory.CreateDirectory(Path.Combine(root, "wwwroot"));
        return root;
    }

    /// <summary>Found by walking up for the solution file, so this works from wherever the test binary is run.</summary>
    private static string RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Aerie.slnx")))
                return d.FullName;

        throw new InvalidOperationException("Could not find the repository root (no src/Aerie.slnx above the test binary).");
    }

    private static string Named(NpgsqlConnectionStringBuilder template, string database) =>
        new NpgsqlConnectionStringBuilder(template.ConnectionString) { Database = database }.ConnectionString;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Database names are composed here from the variable's own, so there is nothing a caller could inject; quoting is so an unusual one still parses.</summary>
    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
