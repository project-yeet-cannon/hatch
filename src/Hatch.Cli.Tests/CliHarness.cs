namespace Hatch.Cli.Tests;

/// <summary>
/// One test's conversational CLI: a stub wire, a transcript, and replies from a
/// list.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="Harness"/>. That one builds a temporary
/// checkout, a session runner and a workspace because <c>work</c> spawns an
/// agent in a tree; the fourteen commands here make one request and say a
/// sentence about the answer, and giving them a checkout they never look at
/// would hide the thing AERIE-934 changed - that they no longer need one.
/// </remarks>
public sealed class CliHarness : IDisposable
{
    public Wire Wire { get; } = new();
    public Transcript Say { get; } = new();
    public Replies In { get; }

    private readonly HatchClient _client;

    public CliHarness(string key = "hatch_ak_test", TimeProvider? clock = null, string?[]? replies = null)
    {
        In = new Replies(replies ?? []);

        var settings = new Settings { Base = "https://hatch.example", Key = key, HeartbeatSeconds = 0 };
        _client = new HatchClient(settings, "test:/checkout", Wire);

        Cli = new Cli(new Board(_client), Say, In, settings, "test:/checkout")
        {
            Clock = clock ?? TimeProvider.System,
        };
    }

    public Cli Cli { get; }

    public Board Board => Cli.Board;

    public HatchClient Client => _client;

    /// <summary>Everything said on standard output, as one string, for the assertions about shape.</summary>
    public string Said => string.Join("\n", Say.Said);

    /// <summary>And everything said on standard error.</summary>
    public string Complained => string.Join("\n", Say.Complained);

    public void Dispose()
    {
        _client.Dispose();
        Wire.Dispose();
    }
}

/// <summary>A clock a test can put at a particular instant, with a particular offset.</summary>
public sealed class FrozenClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
}
