using System.Diagnostics;

namespace Aerie.Hatch.Tests;

/// <summary>
/// The claude CLI, replaced by whatever the test wants it to be: a run that
/// finishes, one that fails, one that sits there long enough for a heartbeat to
/// be refused underneath it.
/// </summary>
public sealed class FakeSessions : ISessionRunner
{
    /// <summary>Every spawn, in order - so a test can assert that there was not one.</summary>
    public List<SessionRequest> Spawned { get; } = [];

    /// <summary>Every attached spawn.</summary>
    public List<SessionRequest> Attached { get; } = [];

    /// <summary>Set once the session is under way, so a test can act while it runs.</summary>
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What a session does. The default emits a few events and finishes well.</summary>
    public Func<SessionRequest, Action<string>?, CancellationToken, Task<SessionResult>> Behaviour { get; set; } =
        (_, onLine, _) =>
        {
            onLine?.Invoke(Fixtures.Init());
            onLine?.Invoke(Fixtures.ToolUse("Bash", "make test-api"));
            onLine?.Invoke(Fixtures.Result(said: "```work-log\nDid a thing\n\nIn detail.\n```"));
            return Task.FromResult(new SessionResult(0, ""));
        };

    /// <summary>A session that runs until its token is cancelled, which is what a lost lease does to one.</summary>
    public static Func<SessionRequest, Action<string>?, CancellationToken, Task<SessionResult>> UntilStopped(
        Action? onceRunning = null) =>
        async (_, onLine, ct) =>
        {
            onLine?.Invoke(Fixtures.Init());
            onLine?.Invoke(Fixtures.ToolUse("Bash", "make test-api"));
            onceRunning?.Invoke();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                // Killed, which is the outcome under test. A killed run reports
                // nothing, exactly as a TERM'd CLI does not get to write a
                // result event.
                return new SessionResult(143, "");
            }

            return new SessionResult(0, "");
        };

    public Task<SessionResult> RunAsync(SessionRequest request, Action<string>? onLine, CancellationToken ct)
    {
        Spawned.Add(request);
        Started.TrySetResult();
        return Behaviour(request, onLine, ct);
    }

    public Task<int> AttachAsync(SessionRequest request, CancellationToken ct)
    {
        Attached.Add(request);
        Started.TrySetResult();
        return Task.FromResult(0);
    }
}

/// <summary>The tree between increments, without a remote to fetch from.</summary>
public sealed class FakeWorkspace : IWorkspace
{
    public Reset Answer { get; set; } = Reset.Ready;

    /// <summary>How many times a pass asked for the tree to be made current.</summary>
    public int Prepared { get; private set; }

    /// <summary>
    /// Run at the moment the reset is asked for, so a test can look at what had
    /// already happened by then. The order is the acceptance criterion: a ticket
    /// is claimed before the fetch, not after it.
    /// </summary>
    public Action? Watching { get; set; }

    public Reset Prepare()
    {
        Prepared++;
        Watching?.Invoke();
        return Answer;
    }
}

/// <summary>
/// The loop's own source, without a tree to change: a test sets what the next
/// read answers, at the moment a reset would have pulled it.
/// </summary>
public sealed class FakeSelf : ISelf
{
    /// <summary>What the loop is made of, until a test says it is made of something else.</summary>
    public SelfPrint Print { get; set; } = Of(("src/Aerie.Hatch/GoToWork.cs", "before"));

    /// <summary>How many times the loop read itself.</summary>
    public int Taken { get; private set; }

    /// <summary>A print over the named files and the hashes they are standing in for.</summary>
    public static SelfPrint Of(params (string Path, string Hash)[] files)
    {
        var map = files.ToDictionary(f => f.Path, f => f.Hash, StringComparer.Ordinal);
        var ordered = map.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={f.Value}");
        return new SelfPrint(map, string.Join('\n', ordered));
    }

    public SelfPrint Take()
    {
        Taken++;
        return Print;
    }
}

/// <summary>One test's runner: a stub wire, a stub session, a temporary tree.</summary>
public sealed class Harness : IDisposable
{
    /// <summary>
    /// Short enough that a heartbeat happens inside a test rather than in a
    /// minute's time. The interval a real run uses is the server's TTL divided
    /// down - see <see cref="Claim.Interval"/>, which has its own test.
    /// </summary>
    public static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(25);

    public Wire Wire { get; } = new();
    public FakeSessions Sessions { get; } = new();
    public Transcript Say { get; } = new();
    public string Root { get; }
    public string Temp { get; }

    private readonly HatchClient _client;

    public Harness(TimeSpan? heartbeat = null)
    {
        Temp = Directory.CreateTempSubdirectory("hatch-test-").FullName;
        Root = Path.Combine(Temp, "checkout");
        Directory.CreateDirectory(Path.Combine(Root, ".git"));

        var settings = new Settings { Base = "https://hatch.example", Key = "aerie_ak_test", HeartbeatSeconds = 0 };
        _client = new HatchClient(settings, Wire);

        Runtime = new Runtime(
            Settings: settings,
            Board: new Board(_client),
            Sessions: Sessions,
            Say: Say,
            Root: Root,
            RunnerName: "test:/checkout",
            TempDirectory: Temp,
            Heartbeat: heartbeat ?? Beat)
        {
            Workspace = () => Workspace,
            Self = () => Self,
        };
    }

    /// <summary>
    /// Where a night's totals would be handed on. Named but not written: the
    /// supervisor makes the path and the runner writes to it only when it is
    /// asking to come back.
    /// </summary>
    public string NightState => Path.Combine(Temp, "night.json");

    /// <summary>A loop that is being watched by a supervisor, and so may ask to restart.</summary>
    public Runtime Supervised => Runtime with { NightStatePath = NightState };

    /// <summary>The loop's own source, as the loop finds it.</summary>
    public FakeSelf Self { get; } = new();

    /// <summary>The tree, as the pass finds it. Ready unless a test says otherwise.</summary>
    public FakeWorkspace Workspace { get; } = new();

    public Runtime Runtime { get; }

    public Board Board => Runtime.Board;

    public HatchClient Client => _client;

    /// <summary>
    /// Waits for something to become true, or fails saying what it was waiting
    /// for. Everything here is a race by construction; a fixed sleep would be
    /// either slow or flaky and usually both.
    /// </summary>
    public static async Task Eventually(Func<bool> until, string what, int withinMs = 5_000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < withinMs)
        {
            if (until()) return;
            await Task.Delay(5);
        }

        Assert.Fail($"waited {withinMs}ms for {what}, and it did not happen");
    }

    public void Dispose()
    {
        _client.Dispose();
        Wire.Dispose();
        try
        {
            Directory.Delete(Temp, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that will not go is the operating system's
            // to tidy, not a test failure.
        }
    }
}
