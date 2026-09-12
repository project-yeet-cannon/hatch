using System.Diagnostics;

namespace Hatch.Cli;

/// <summary>
/// One loop per checkout, and this is what says so.
/// </summary>
/// <remarks>
/// <para>A directory, because creating one is atomic on every filesystem this
/// could land on and a file written with a redirect is not. Under the temporary
/// directory rather than in the repository: a lock in a tracked tree is a lock
/// somebody commits, and a lock that outlives a reboot is one somebody has to
/// come and clear by hand.</para>
///
/// <para>Per checkout and not per machine, which is the line that lets two
/// loops start. It was per machine while a claim did not exist, because two
/// loops with no way to divide the board between them would both take the same
/// ticket; now the board divides it, and the only thing two loops in <em>one
/// tree</em> would still collide over is the tree - one increment's reset
/// landing in the middle of another's branch.</para>
/// </remarks>
public sealed class LoopLock : IDisposable
{
    private readonly string _dir;
    private bool _held;

    private LoopLock(string dir)
    {
        _dir = dir;
        _held = true;
    }

    /// <summary>Where the lock for this checkout lives.</summary>
    public static string PathFor(string root, string tempDirectory) =>
        Path.Combine(tempDirectory, $"hatch-go-to-work-{Checkout.Fingerprint(root)}.lock");

    /// <summary>
    /// Take it, or say who has it. <paramref name="alive"/> is how "is that pid
    /// still there" is asked, so a test can answer it without a process.
    /// </summary>
    /// <returns>The lock, or null - in which case <paramref name="refusal"/> says why.</returns>
    public static LoopLock? Take(
        string root, string tempDirectory, out string refusal, Func<int, bool>? alive = null)
    {
        alive ??= StillRunning;
        var dir = PathFor(root, tempDirectory);
        refusal = "";

        if (TryCreate(dir, out var taken)) return taken;

        // A lock whose owner is gone - killed outright, or a machine that
        // rebooted out from under it - is not a lock, it is litter. Clearing it
        // is the difference between a loop that survives a crash and one that
        // has to be let back in by hand.
        var pid = ReadPid(dir);
        if (pid is { } owner && alive(owner))
        {
            refusal = string.Join('\n',
                $"hatch: a go-to-work is already running in {root}, pid {owner}.",
                "hatch: one loop per checkout is the premise - join that one, stop it, or work in another checkout.");
            return null;
        }

        Console.Error.WriteLine($"hatch: clearing a stale lock left by pid {pid?.ToString() ?? "?"}");
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Somebody else got there first, which the retry below will find out.
        }

        if (TryCreate(dir, out var second)) return second;

        // Two loops that both found the lock stale, and this is the one that
        // lost the race for it. The other is running; that is the right outcome
        // either way.
        refusal = $"hatch: could not take the lock at {dir}";
        return null;
    }

    /// <summary>
    /// The atomic half. Creating the directory is not it -
    /// <c>CreateDirectory</c> succeeds quietly on one that is already there -
    /// so the pid file is what claims the lock: <c>CreateNew</c> fails on a
    /// file that exists, and the loser of a race is whoever it fails for.
    /// </summary>
    private static bool TryCreate(string dir, out LoopLock? taken)
    {
        taken = null;
        try
        {
            Directory.CreateDirectory(dir);

            using (var pid = new FileStream(
                       Path.Combine(dir, "pid"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(pid))
            {
                writer.Write(Environment.ProcessId);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        taken = new LoopLock(dir);
        return true;
    }

    private static int? ReadPid(string dir)
    {
        try
        {
            return int.TryParse(File.ReadAllText(Path.Combine(dir, "pid")).Trim(), out var pid) ? pid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether that pid is still there. Asking for the process rather than
    /// signalling it, which is what <c>kill -0</c> was doing.
    /// </summary>
    private static bool StillRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Give it up. Safe to call twice, which on an interrupt it will be.</summary>
    public void Dispose()
    {
        if (!_held) return;
        _held = false;

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Already gone, or not ours to remove. Either way there is nothing
            // useful to say on the way out of a run.
        }
    }
}
