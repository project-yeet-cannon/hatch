using System.Runtime.InteropServices;
using System.Text;
using Aerie.Hatch;

// The whole CLI, as one program.
//
// It began as the two commands that spawn an agent - `work` and `go-to-work` -
// because a claim is a lease with a clock on it and none of that could be tested
// in a shell script. AERIE-934 brought the other fourteen across for a different
// reason: an operator who is not in this repository has no scripts/hatch.sh, and
// a tracker that can only be reached from one clone is a tracker for one person.
// So this is `hatch`, installed once and run anywhere, and hatch.sh is a door
// into it. See docs/hatch.md, "Where the loop lives".

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
var rest = args[1..];

if (!Program.Commands.Contains(command))
{
    say.Complain($"hatch: no such command \"{command}\" - `hatch --help` lists them.");
    return 1;
}

var environment = Environment.GetEnvironmentVariables()
    .Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => e.Value as string);

// Where the increment happens. hatch.sh names it, because it knows where it
// lives; a runner started by hand finds it by walking up from wherever it was
// started, which is what every other tool in a repository does.
//
// Only `work` and `go-to-work` require one. The other fourteen are one request
// and a sentence about the answer, and `hatch board` from a directory that has
// never been a repository has to work - which is most of what AERIE-934 is
// about.
var here = Directory.GetCurrentDirectory();
var root = Checkout.Find(environment.GetValueOrDefault("HATCH_ROOT"), here);

if (root is null && command is "work" or "go-to-work")
{
    say.Complain("hatch: this is not a git repository, and a ticket is about a codebase.");
    say.Complain("hatch:   run it inside a checkout, or name one in HATCH_ROOT.");
    return 1;
}

// This checkout's own settings, at higher precedence than the per-user file, so
// a repository that pins its own origin keeps it. Absent outside one.
var checkoutEnv = root is null ? null : Path.Combine(root, "scripts", ".env");

// `config` is the one command that has to run before there is anything to
// require, so it reads the layers itself rather than through a load that would
// refuse for want of the origin it is there to ask for.
if (command == "config")
{
    var configured = Settings.Layers(checkoutEnv, environment)("HATCH_RUNNER").Value;
    return await new ConfigCommand(
            say, new Input(), environment, checkoutEnv, Checkout.Runner(configured, Host(), root ?? here))
        .RunAsync(rest, CancellationToken.None);
}

if (!Settings.TryLoad(checkoutEnv, environment, out var settings, out var missing))
{
    say.Complain(missing);
    return 1;
}

var runnerName = Checkout.Runner(settings.Runner, Host(), root ?? here);

using var client = new HatchClient(settings, runnerName);
var board = new Board(client);

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
    if (command is "work" or "go-to-work")
    {
        var runtime = new Runtime(
            Settings: settings,
            Board: board,
            Sessions: new ClaudeSessionRunner(settings.ClaudeBin),
            Say: say,
            Root: root!,
            RunnerName: runnerName,
            TempDirectory: Path.GetTempPath())
        {
            // Set by the supervisor in scripts/hatch.sh and by nobody else,
            // which is how a runner started by hand knows there is nothing
            // standing over it to build the new source and run it again. See
            // docs/hatch.md, "What it stops for".
            NightStatePath = environment.GetValueOrDefault("HATCH_NIGHT_STATE"),
        }.WithGit();

        return command == "work"
            ? await new WorkCommand(runtime).RunAsync(rest, cancelling.Token)
            : await new GoToWorkCommand(runtime).RunAsync(rest, cancelling.Token);
    }

    var cli = new Cli(board, say, new Input(), settings, runnerName);

    return command switch
    {
        "board" => await new BoardCommands(cli).BoardAsync(rest, cancelling.Token),
        "next" => await new BoardCommands(cli).NextAsync(rest, cancelling.Token),
        "queue" => await new BoardCommands(cli).QueueAsync(rest, cancelling.Token),
        "show" => await new IssueCommands(cli).ShowAsync(rest, cancelling.Token),
        "start" => await new IssueCommands(cli).StartAsync(rest, cancelling.Token),
        "move" => await new IssueCommands(cli).MoveAsync(rest, cancelling.Token),
        "comment" => await new IssueCommands(cli).CommentAsync(rest, cancelling.Token),
        "pr" => await new IssueCommands(cli).PrAsync(rest, cancelling.Token),
        "depends" => await new DependsCommand(cli).RunAsync(rest, cancelling.Token),
        "ask" => await new QuestionCommands(cli).AskAsync(rest, cancelling.Token),
        "questions" => await new QuestionCommands(cli).QuestionsAsync(rest, cancelling.Token),
        "answer" => await new QuestionCommands(cli).AnswerAsync(rest, cancelling.Token),
        "api" => await new ApiCommand(cli).RunAsync(rest, cancelling.Token),
        "runner-claude-token" => await new ClaudeTokenCommand(cli).RunAsync(rest, cancelling.Token),

        // Unreachable: the name was checked against the same table above.
        _ => 1,
    };
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

internal partial class Program
{
    /// <summary>
    /// The commands something other than a person runs, and no session is ever
    /// told about.
    /// </summary>
    /// <remarks>
    /// Real commands, in <see cref="Commands"/> and in <see cref="Usage"/> like
    /// every other - there is no hidden verb here. What this list is for is the
    /// block in docs/hatch-at-home.md that a friend pastes into their own
    /// repository's <c>CLAUDE.md</c>: that block is the contract an agent
    /// works to, and <c>runner-claude-token</c> is the container entrypoint's
    /// own plumbing (containers/hatch-runner/entrypoint.sh). Naming it there
    /// would be telling every session in the house about a command that prints
    /// a credential, for no work it could ever do with it. DocsContractTests
    /// holds both halves of that: the rest are named in the block, and these
    /// are deliberately not.
    /// </remarks>
    public static readonly string[] Internal =
    [
        "runner-claude-token",
    ];

    /// <summary>
    /// Every command, in one place - so a name that is not one of them is
    /// refused before anything is loaded, and so the dispatch below and this
    /// list cannot drift apart.
    /// </summary>
    public static readonly string[] Commands =
    [
        "config", "board", "next", "queue", "show", "start", "move", "comment", "pr",
        "depends", "ask", "questions", "answer", "api", "work", "go-to-work",
        .. Internal,
    ];

    public static readonly string[] Usage =
    [
        "hatch - the house tracker, from a terminal",
        "",
        "  hatch config                 ask for the origin and the key, and write them",
        "  hatch config --show          what is set, and which layer it came from",
        "  hatch board                  the columns, and how many cards in each",
        "  hatch next                   top workable card of \"todo\"",
        "  hatch next \"in progress\"      ...or of any column",
        "  hatch queue                  every card a pass would look at, and why",
        "  hatch queue AER-1            ...under one epic",
        "  hatch show AER-12            the brief, plus its comments",
        "  hatch start AER-12           move it to \"in progress\"",
        "  hatch move AER-12 todo       ...or to any non-terminal column",
        "  hatch comment AER-12 \"sha abc123 on branch aer-12-thing\"",
        "  hatch pr AER-12              where it is being reviewed",
        "  hatch pr AER-12 https://...  ...or say where, having opened one",
        "  hatch pr AER-12 --clear      ...or take it off the one it has",
        "  hatch depends AER-12         what it waits on, and what waits on it",
        "  hatch depends AER-12 AER-11  AER-12 waits on AER-11",
        "  hatch depends AER-12 --remove AER-11        ...no longer",
        "  hatch ask AER-12 \"how should retries be scoped?\" \\",
        "      --recommend \"Per-node: one budget per node, so a slow node cannot starve\" \\",
        "      --option \"Global: one budget for the drain, simpler to reason about\"",
        "  hatch questions              everything waiting on an answer",
        "  hatch questions AER-12       ...or just this ticket's",
        "  hatch answer                 answer them, one at a time, here",
        "  hatch api GET /api/hatch/issues?statusId=2",
        "  hatch api PATCH /api/hatch/issues/AER-12 '{\"dueAt\":\"2026-10-01\"}'",
        "",
        "The one the container runner's entrypoint calls, and nobody types:",
        "",
        "  hatch runner-claude-token    the Claude token this Hatch holds, decoded",
        "",
        "The two that spawn an agent, and the only two that need a git checkout:",
        "",
        "  hatch work                   one increment on the next thing due",
        "  hatch work AER-12            ...or on this one",
        "  hatch work --under AER-1     ...or on the next thing under one epic",
        "  hatch work -i AER-12         ...in a session you sit in",
        "  hatch work --quiet           ...saying nothing until it is finished",
        "  hatch work --model opus --effort xhigh AER-12",
        "  hatch work --dry-run         print the prompt, spawn nothing",
        "  hatch go-to-work             increments, back to back, until told to stop",
        "  hatch go-to-work --once      ...one pass, and out",
        "  hatch go-to-work --under AER-1 --interval 300",
        "  hatch go-to-work --max-runs 5 --max-spend 20 --until 08:00",
        "  hatch go-to-work --stop-file /tmp/stop",
        "  hatch go-to-work --restart-after 60   ...coming back as a newer build that often",
        "  hatch go-to-work --restart-after 0    ...only when its own source changed",
        "  hatch go-to-work --no-restart         ...never coming back as a newer one",
        "",
        "Every command takes -h for its own usage block.",
        "",
        "Settings, highest first: an exported variable, then scripts/.env in the",
        "checkout you are standing in, then the per-user file `config` writes.",
        "",
        "  AERIE_BASE         https://hatch.<your domain>",
        "  AERIE_HATCH_KEY    aerie_ak_... Optional against a Hatch with its wall off,",
        "                     where calls name themselves with a runner header instead",
        "  HATCH_CLAUDE_BIN   the claude CLI, if it is not on PATH",
        "  HATCH_BASE_BRANCH  the trunk go-to-work resets to between increments",
        "  HATCH_RUNNER       what the board calls this runner (default host:/path)",
        "  HATCH_ROOT         the checkout to work in (default: upwards from here)",
        "  HATCH_HEARTBEAT    seconds of silence before the renderer says what it is waiting on",
        "  HATCH_NIGHT_STATE  where a night's totals are handed to the loop that restarts into",
        "",
        "A go-to-work whose own source changes under it asks to be restarted as the new",
        "build, and something outside it has to rebuild and run it again. A hatch started",
        "with nobody standing over it does not ask: it is the loop it started as.",
    ];
}
