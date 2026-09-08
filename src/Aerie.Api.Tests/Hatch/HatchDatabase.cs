using Aerie.Api.Modules.Hatch;
using Microsoft.EntityFrameworkCore;

namespace Aerie.Api.Tests.Hatch;

/// <summary>
/// A real Postgres for the tests that need one, or an honest absence.
///
/// <para>It exists for one feature. The claim (<see cref="IssueClaims"/>) is
/// built out of conditional <c>UPDATE</c>s, and the guarantee it makes lives in
/// the <c>WHERE</c> clause rather than in anything C# does around it. EF's
/// in-memory provider refuses <c>ExecuteUpdateAsync</c> outright and the SQLite
/// provider cannot translate the <c>DateTimeOffset</c> comparison the expiry
/// rule is, so both would have to be tested against something other than what
/// ships - which is the failure this lane prevents, and the same argument the
/// trading silo's queue lane makes one job over in ci.yml.</para>
///
/// <para>The rest of the suite is untouched: everything that is not a
/// conditional write still runs on the in-memory provider, with no database and
/// no service container.</para>
/// </summary>
internal static class HatchDatabase
{
    /// <summary>
    /// Where the scratch database is. <b>It is dropped and recreated</b>, so
    /// this must never name a database anybody cares about - which is why it is
    /// demanded explicitly rather than discovered, and why nothing here falls
    /// back to the connection string the app ships with.
    /// </summary>
    /// <remarks>
    /// Either an Npgsql keyword string (<c>Host=…;Database=…</c>) or a
    /// <c>postgresql://</c> URI, because the first is what .NET writes and the
    /// second is what everything else does.
    /// </remarks>
    public const string Variable = "AERIE_TEST_DATABASE_URL";

    private static readonly SemaphoreSlim Once = new(1, 1);

    private static string? prepared;

    public static bool Available => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    /// <summary>
    /// The connection string, against a schema that exists and holds nothing.
    /// The database is created once per process and emptied per call - dropping
    /// it between tests would race the connection pool, and a truncate is both
    /// faster and honest about what isolation these tests need.
    /// </summary>
    public static async Task<string> PrepareAsync()
    {
        var configured = Environment.GetEnvironmentVariable(Variable)
            ?? throw new InvalidOperationException($"{Variable} is unset");

        var connectionString = ToConnectionString(configured.Trim());

        await Once.WaitAsync();
        try
        {
            await using var db = new HatchContext(
                new DbContextOptionsBuilder<HatchContext>().UseNpgsql(connectionString).Options);

            if (prepared != connectionString)
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                prepared = connectionString;
            }
            else
            {
                // Every table the context knows about, taken from the model
                // rather than listed here - a list would be one more thing to
                // remember on the day somebody adds a table.
                var tables = db.Model.GetEntityTypes()
                    .Select(e => $"{Quote(e.GetSchema() ?? HatchContext.Schema)}.{Quote(e.GetTableName()!)}")
                    .Distinct();

                // Composed rather than interpolated at the call site: every
                // name here comes from the EF model, so there is nothing to
                // parameterise and nothing a caller could inject.
                var truncate = "TRUNCATE " + string.Join(", ", tables) + " RESTART IDENTITY CASCADE";
                await db.Database.ExecuteSqlRawAsync(truncate);
            }
        }
        finally
        {
            Once.Release();
        }

        return connectionString;
    }

    /// <summary>
    /// A URI turned into what Npgsql wants, or a keyword string left alone.
    /// Both forms are accepted because the variable is set from two directions:
    /// a Makefile target here, and a service container in CI whose credentials
    /// read naturally as a URL.
    /// </summary>
    internal static string ToConnectionString(string configured)
    {
        if (!configured.Contains("://")) return configured;

        var uri = new Uri(configured);
        var credentials = uri.UserInfo.Split(':', 2);

        return string.Join(';',
        [
            $"Host={uri.Host}",
            $"Port={(uri.Port > 0 ? uri.Port : 5432)}",
            $"Database={uri.AbsolutePath.TrimStart('/')}",
            $"Username={Uri.UnescapeDataString(credentials[0])}",
            .. credentials.Length > 1 ? new[] { $"Password={Uri.UnescapeDataString(credentials[1])}" } : [],
        ]);
    }

    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
