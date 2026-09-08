using System.Diagnostics;
using System.Text;

namespace Aerie.Hatch;

/// <summary>What to spawn, and where.</summary>
/// <param name="Quiet">
/// No live rendering. The facts then come from the CLI's own summary instead -
/// one object at the end carrying the session id, the cost, and the last thing
/// the session said.
/// </param>
public sealed record SessionRequest(
    string Bin, string Root, string Model, string Effort, string Prompt, bool Quiet);

/// <param name="Output">Everything the CLI wrote, on a quiet run. Empty on a streamed one, which was rendered as it arrived.</param>
public sealed record SessionResult(int ExitCode, string Output);

/// <summary>
/// The spawn, behind an interface so that everything around it - the claim, the
/// heartbeat, what happens when a lease is lost - can be tested without a CLI,
/// an API key or four minutes of wall clock.
/// </summary>
public interface ISessionRunner
{
    /// <summary>
    /// Runs one increment's session. Cancelling kills it, which is what a
    /// refused heartbeat does.
    /// </summary>
    Task<SessionResult> RunAsync(SessionRequest request, Action<string>? onLine, CancellationToken ct);

    /// <summary>
    /// The same ticket, the same playbook, the same budget, in a session
    /// somebody is sitting in front of - stdio is the terminal's.
    /// </summary>
    Task<int> AttachAsync(SessionRequest request, CancellationToken ct);
}

/// <summary>The claude CLI, actually spawned.</summary>
public sealed class ClaudeSessionRunner : ISessionRunner
{
    public async Task<SessionResult> RunAsync(SessionRequest request, Action<string>? onLine, CancellationToken ct)
    {
        // bypassPermissions because in print mode nothing can answer a prompt:
        // any permission this did not anticipate becomes a silent denial in the
        // middle of a run nobody is watching. That is a deliberate grant, and
        // the reason `work` is a command an operator types rather than
        // something a cron job does.
        var start = Base(request);
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add("--permission-mode");
        start.ArgumentList.Add("bypassPermissions");

        if (request.Quiet)
        {
            start.ArgumentList.Add("--output-format");
            start.ArgumentList.Add("json");
        }
        else
        {
            start.ArgumentList.Add("--output-format");
            start.ArgumentList.Add("stream-json");
            start.ArgumentList.Add("--verbose");
        }

        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;

        using var process = new Process { StartInfo = start };
        process.Start();

        // The prompt goes in on stdin rather than as an argument - it is long,
        // and an argument list is the one place where "long" has a limit worth
        // avoiding.
        var writing = Task.Run(async () =>
        {
            await process.StandardInput.WriteAsync(request.Prompt);
            process.StandardInput.Close();
        }, CancellationToken.None);

        var collected = new StringBuilder();
        var reading = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                if (onLine is null) collected.AppendLine(line);
                else onLine(line);
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
        }

        // Whatever was written before the kill is still worth having, and the
        // reader ends of its own accord once the pipe closes.
        await Task.WhenAll(writing, reading).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);

        return new SessionResult(process.ExitCode, collected.ToString());
    }

    public async Task<int> AttachAsync(SessionRequest request, CancellationToken ct)
    {
        // No bypassPermissions - there is somebody here to answer a prompt, and
        // the grant the unattended path makes exists only because in print mode
        // there is not.
        //
        // And the prompt is an argument rather than stdin, because stdin is the
        // terminal: it is what the operator is about to type into.
        var start = Base(request);
        start.ArgumentList.Add(request.Prompt);

        using var process = new Process { StartInfo = start };
        process.Start();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        return process.ExitCode;
    }

    private static ProcessStartInfo Base(SessionRequest request)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.Bin,
            WorkingDirectory = request.Root,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(request.Model);
        start.ArgumentList.Add("--effort");
        start.ArgumentList.Add(request.Effort);
        start.ArgumentList.Add("--add-dir");
        start.ArgumentList.Add(request.Root);
        return start;
    }

    /// <summary>
    /// The whole tree, because a session has spawned tools of its own and a
    /// lease that is over should not leave a `make test` running for four more
    /// minutes on a ticket somebody else now holds.
    /// </summary>
    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or SystemException)
        {
            // It ended between the question and the answer, which is the
            // outcome that was being asked for.
        }
    }

    /// <summary>
    /// The CLI that runs the increment.
    /// </summary>
    /// <remarks>
    /// No search of an editor extension's bundle, though a binary does live in
    /// one: it is an implementation detail of a program that updates itself
    /// weekly, and a loop built on that path breaks on somebody else's release
    /// schedule. Install the CLI, or name it once in <c>HATCH_CLAUDE_BIN</c>.
    /// </remarks>
    public static bool TryFind(string? configured, out string bin, out string refusal)
    {
        refusal = "";

        if (!string.IsNullOrWhiteSpace(configured))
        {
            bin = configured;
            if (File.Exists(bin)) return true;

            refusal = $"hatch: HATCH_CLAUDE_BIN is not there: {bin}";
            return false;
        }

        bin = "claude";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;

            foreach (var name in OperatingSystem.IsWindows() ? (string[])["claude.exe", "claude.cmd", "claude"] : ["claude"])
            {
                var candidate = Path.Combine(dir, name);
                if (!File.Exists(candidate)) continue;

                bin = candidate;
                return true;
            }
        }

        refusal = string.Join('\n',
            "hatch: no claude CLI on PATH.",
            "",
            "  Install it, or point at one you have:",
            "      export HATCH_CLAUDE_BIN=/path/to/claude",
            "",
            "  The binary inside an editor extension's directory will work, but it moves",
            "  with every update - name it here and expect to rename it, or install the",
            "  standalone CLI and forget about it.");
        return false;
    }
}
