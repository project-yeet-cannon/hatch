namespace Aerie.Api.Tests.Jobs;

/// <summary>
/// The one thing every Quartz test in this assembly shares whether it asked to
/// or not: <c>Quartz.Logging.LogProvider</c>'s static, one per process.
///
/// <para>xUnit gives each test class its own collection and runs collections in
/// parallel, so without this the class that boots a host - binding that static
/// to the host's <c>LoggerFactory</c> - runs alongside the class that builds
/// schedulers through it. The window is small and real: between the host being
/// disposed and the static being cleared, every <c>GetScheduler</c> in the
/// process resolves a logger off a disposed factory.</para>
///
/// <para>Naming both classes into one collection is where xUnit enforces that,
/// and it is the whole cost - five fast tests and two that need a Postgres,
/// which no longer overlap.</para>
/// </summary>
[CollectionDefinition(Name)]
public class QuartzSchedulerCollection
{
    public const string Name = "Quartz scheduler";
}
