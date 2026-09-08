using Aerie.Api.Jobs;
using Aerie.Api.Services.DeviceMapping;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;

namespace Aerie.Api.Tests.Jobs;

/// <summary>
/// The house gate: Home Assistant is what makes an installation a house, and a
/// job that serves the house is not scheduled on an installation that has none.
///
/// <para>Against a real in-memory Quartz scheduler rather than a recording
/// stub. IScheduler is forty-odd members, and a stub of it would assert that
/// WireUpJobs made certain <em>calls</em>; what the story is about is the
/// state those calls leave behind - whether a trigger exists - which is what
/// this asserts instead.</para>
/// </summary>
public class JobsInitTests
{
    /// <summary>
    /// A stand-in for whichever of SampleChannels/ReconcileCommands/… is being
    /// reasoned about. The gate reads Name, Group, Interval and ServesTheHouse
    /// and nothing else, so a fake carrying those four is the whole job as far
    /// as JobsInit is concerned - and using one keeps these tests off the four
    /// real jobs' constructor dependencies.
    /// </summary>
    private class FakeJob(string name, TimeSpan interval, bool servesTheHouse) : IAerieJob
    {
        public string Name { get; } = name;

        public string Group => "Aerie.Api.Tests";

        public TimeSpan Interval { get; } = interval;

        public bool ServesTheHouse { get; } = servesTheHouse;

        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    /// <summary>Resolves a connection, resolves none, or fails to read at all - the third being the database the migrate step has not touched.</summary>
    private class StubConnections(HomeAssistantConnection? connection = null, bool throws = false) : IHomeAssistantConnectionManager
    {
        public HomeAssistantConnection? Connection { get; set; } = connection;

        public bool Throws { get; set; } = throws;

        public Task<HomeAssistantConnection?> ResolveAsync(CancellationToken ct) =>
            Throws
                ? Task.FromException<HomeAssistantConnection?>(new InvalidOperationException("42P01: relation \"SiteSettings\" does not exist"))
                : Task.FromResult(Connection);

        public Task ApplyAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static readonly HomeAssistantConnection AHouse = new("homeassistant.invalid", "8123", "token");

    private static readonly FakeJob HouseJob = new("HouseJob", TimeSpan.FromMinutes(1), servesTheHouse: true);

    private static readonly FakeJob AnotherHouseJob = new("AnotherHouseJob", TimeSpan.FromMinutes(15), servesTheHouse: true);

    private static readonly FakeJob HouselessJob = new("HouselessJob", TimeSpan.FromMinutes(5), servesTheHouse: false);

    /// <summary>
    /// A scheduler of its own per test, keyed on a fresh name: StdSchedulerFactory
    /// hands back the same instance for the same name, and Quartz's default job
    /// store is in-memory, so this is a real scheduler with nothing behind it.
    /// Never started - registration is what is under test, not execution.
    /// </summary>
    private static async Task<IScheduler> NewSchedulerAsync() =>
        await new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"JobsInitTests-{Guid.NewGuid():N}",
        }).GetScheduler();

    private static JobsInit Init(IScheduler scheduler, IHomeAssistantConnectionManager connections, params IAerieJob[] jobs) =>
        new(scheduler, jobs, connections, NullLogger<JobsInit>.Instance);

    [Fact]
    public async Task WithNoConnection_NoHouseJobIsScheduled()
    {
        var scheduler = await NewSchedulerAsync();

        await Init(scheduler, new StubConnections(), HouseJob, AnotherHouseJob).WireUpJobs();

        Assert.False(await scheduler.CheckExists(new JobKey(HouseJob.Name, HouseJob.Group)));
        Assert.False(await scheduler.CheckExists(new JobKey(AnotherHouseJob.Name, AnotherHouseJob.Group)));
    }

    [Fact]
    public async Task WithAConnection_EveryHouseJobIsScheduledOnItsOwnInterval()
    {
        var scheduler = await NewSchedulerAsync();

        await Init(scheduler, new StubConnections(AHouse), HouseJob, AnotherHouseJob).WireUpJobs();

        foreach (var job in new[] { HouseJob, AnotherHouseJob })
        {
            Assert.True(await scheduler.CheckExists(new JobKey(job.Name, job.Group)));

            var trigger = Assert.Single(await scheduler.GetTriggersOfJob(new JobKey(job.Name, job.Group)));
            Assert.Equal(job.Interval, Assert.IsAssignableFrom<ISimpleTrigger>(trigger).RepeatInterval);
        }
    }

    /// <summary>
    /// What keeps the property honest the day somebody adds a job that does not
    /// serve the house: the gate has to be reading ServesTheHouse rather than
    /// "is there a connection".
    /// </summary>
    [Fact]
    public async Task AJobThatDoesNotServeTheHouse_IsScheduledEitherWay()
    {
        foreach (var connections in new[] { new StubConnections(), new StubConnections(AHouse) })
        {
            var scheduler = await NewSchedulerAsync();

            await Init(scheduler, connections, HouselessJob).WireUpJobs();

            Assert.True(await scheduler.CheckExists(new JobKey(HouselessJob.Name, HouselessJob.Group)));
        }
    }

    /// <summary>
    /// Criterion 7, without a browser: SettingsController calls this on both
    /// sides of the connection's life, so saving one has to schedule and
    /// clearing one has to unschedule - on a scheduler that already has the
    /// other answer written into it.
    /// </summary>
    [Fact]
    public async Task RunningAgainAfterTheConnectionChanges_SchedulesThenUnschedules()
    {
        var scheduler = await NewSchedulerAsync();
        var connections = new StubConnections(AHouse);
        var init = Init(scheduler, connections, HouseJob);

        await init.WireUpJobs();
        Assert.True(await scheduler.CheckExists(new JobKey(HouseJob.Name, HouseJob.Group)));

        // Saved a second time with the same connection: `replace: true` has to
        // make this a no-op rather than an ObjectAlreadyExistsException, because
        // the three settings are written one key at a time.
        await init.WireUpJobs();
        Assert.True(await scheduler.CheckExists(new JobKey(HouseJob.Name, HouseJob.Group)));

        connections.Connection = null;
        await init.WireUpJobs();
        Assert.False(await scheduler.CheckExists(new JobKey(HouseJob.Name, HouseJob.Group)));

        // And back, so nothing about clearing it is one-way.
        connections.Connection = AHouse;
        await init.WireUpJobs();
        Assert.True(await scheduler.CheckExists(new JobKey(HouseJob.Name, HouseJob.Group)));
    }

    /// <summary>
    /// The regression guard for exit 139. A connection manager that throws is
    /// the database the migrate step has not run on yet - SiteSettings does not
    /// exist - and this call is on the startup path, so an exception here is an
    /// exception out of Main. It has to read as "no house this start".
    /// </summary>
    [Fact]
    public async Task AConnectionManagerThatThrows_IsNoHouseRatherThanACrash()
    {
        var scheduler = await NewSchedulerAsync();

        await Init(scheduler, new StubConnections(throws: true), HouseJob, HouselessJob).WireUpJobs();

        Assert.False(await scheduler.CheckExists(new JobKey(HouseJob.Name, HouseJob.Group)));
        Assert.True(await scheduler.CheckExists(new JobKey(HouselessJob.Name, HouselessJob.Group)));
    }

    /// <summary>
    /// The four that ship. Named rather than reflected: this is the list the
    /// story is about, and a test that discovered it would agree with whatever
    /// the code said.
    /// </summary>
    [Theory]
    [InlineData(typeof(SampleChannels))]
    [InlineData(typeof(ReconcileCommands))]
    [InlineData(typeof(SyncCalendarEvents))]
    [InlineData(typeof(SyncOutdoorHazards))]
    public void EveryShippedJob_ServesTheHouse(Type job)
    {
        var property = job.GetProperty(nameof(IAerieJob.ServesTheHouse));
        Assert.NotNull(property);

        // Read off an uninitialized instance rather than a constructed one -
        // every one of these takes half the container, and the property is a
        // constant expression on all four.
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(job);
        Assert.True((bool)property.GetValue(instance)!);
    }
}
