using System.Runtime.InteropServices;
using System.Text;
using Aerie.Hatch;

// The runner: the two commands that spawn an agent, and therefore the two that
// hold a claim on the ticket they spawn it at. Everything conversational -
// board, show, comment, ask, answer - is still scripts/hatch.sh, which is also
// what invokes this; see docs/hatch.md, "Where the loop lives".

try
{
    // The renderer draws a few characters that are not ASCII, and a Windows
    // console left on its default code page turns them into question marks.
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException)
{
    // No console attached - a redirected log, or a service. Nothing to set.
}

var say = new Terminal();

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    say.Lines(Usage);
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
if (command is not ("work" or "go-to-work"))
{
    say.Complain($"hatch: the runner has no command \"{command}\" - it does work and go-to-work.");
    return 1;
}

// Where the increment happens. hatch.sh names it, because it knows where it
// lives; a runner started by hand finds it by walking up from wherever it was
// started, which is what every other tool in a repository does.
var root = Checkout.Find(
    Environment.GetEnvironmentVariable("HATCH_ROOT"), Directory.GetCurrentDirectory());

if (root is null)
{
    say.Complain("hatch: this is not a git repository, and a ticket is about a codebase.");
    say.Complain("hatch:   run it inside a checkout, or name one in HATCH_ROOT.");
    return 1;
}

var environment = Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => e.Value as string);

if (!Settings.TryLoad(Path.Combine(root, "scripts", ".env"), environment, out var settings, out var missing))
{
    say.Complain(missing);
    return 1;
}

using var client = new HatchClient(settings);
var runtime = new Runtime(
    Settings: settings,
    Board: new Board(client),
    Sessions: new ClaudeSessionRunner(),
    Say: say,
    Root: root,
    RunnerName: Checkout.Runner(settings.Runner, Host(), root),
    TempDirectory: Path.GetTempPath());

// Every way out through one door. A signal that is not handled kills the
// process outright, and the increment that ends in an interrupt is exactly the
// one whose claim most needs letting go of - so both are caught, the token is
// cancelled, and the release happens on the way out of the `finally` the
// commands already have. A second one is left to the runtime: somebody pressing
// it twice means now.
using var cancelling = new CancellationTokenSource();
var caught = 0;

using var interrupt = Handle(PosixSignal.SIGINT);
using var terminate = Handle(PosixSignal.SIGTERM);

try
{
    var rest = args[1..];
    return command == "work"
        ? await new WorkCommand(runtime).RunAsync(rest, cancelling.Token)
        : await new GoToWorkCommand(runtime).RunAsync(rest, cancelling.Token);
}
catch (OperationCanceledException)
{
    return 130;
}
catch (HatchException e)
{
    say.Complain(e.Message);
    return 1;
}

PosixSignalRegistration? Handle(PosixSignal signal)
{
    try
    {
        return PosixSignalRegistration.Create(signal, context =>
        {
            if (Interlocked.Increment(ref caught) > 1) return;

            context.Cancel = true;
            say.Complain("");
            say.Complain("hatch: stopping - letting go of the ticket first");
            cancelling.Cancel();
        });
    }
    catch (Exception e) when (e is PlatformNotSupportedException or ArgumentOutOfRangeException)
    {
        // Not every signal exists on every platform. The ones that do are
        // caught; the ones that do not were never going to arrive.
        return null;
    }
}

static string Host()
{
    // The short name, because a runner is read by a person deciding which box to
    // go and look at and the domain is the same on all of them.
    var name = Environment.MachineName;
    var dot = name.IndexOf('.');
    return dot > 0 ? name[..dot] : name;
}

static partial class Program
{
    public static readonly string[] Usage =
    [
        "hatch-runner - the two commands that spawn an agent",
        "",
        "  hatch-runner work                  one increment on the next thing due",
        "  hatch-runner work AER-12           ...or on this one",
        "  hatch-runner work --under AER-1    ...or on the next thing under one epic",
        "  hatch-runner work -i AER-12        ...in a session you sit in",
        "  hatch-runner work --quiet          ...saying nothing until it is finished",
        "  hatch-runner work --model opus --effort xhigh AER-12",
        "  hatch-runner work --dry-run        print the prompt, spawn nothing",
        "  hatch-runner go-to-work            increments, back to back, until told to stop",
        "  hatch-runner go-to-work --once     ...one pass, and out",
        "  hatch-runner go-to-work --under AER-1 --interval 300",
        "  hatch-runner go-to-work --max-runs 5 --max-spend 20 --until 08:00",
        "  hatch-runner go-to-work --stop-file /tmp/stop",
        "",
        "Settings, from scripts/.env or the environment:",
        "",
        "  AERIE_BASE         https://hatch.<your domain>",
        "  AERIE_HATCH_KEY    aerie_ak_...",
        "  HATCH_CLAUDE_BIN   the claude CLI, if it is not on PATH",
        "  HATCH_BASE_BRANCH  the trunk go-to-work resets to between increments",
        "  HATCH_RUNNER       what the board calls this runner (default host:/path)",
        "  HATCH_ROOT         the checkout to work in (default: upwards from here)",
        "  HATCH_HEARTBEAT    seconds of silence before the renderer says what it is waiting on",
        "",
        "Reach it through ./scripts/hatch.sh work and ./scripts/hatch.sh go-to-work,",
        "which is where the rest of the CLI lives.",
    ];
}
