using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aerie.Hatch;

/// <summary>
/// What the loop's own source looked like at a moment: a hash per file, and one
/// digest over the lot.
/// </summary>
/// <remarks>
/// <para>Source and not the built binary, because the restart's whole job is to
/// run <em>new code</em>, and new code arrives as source that something else
/// still has to build. A print taken over the binary would only change once the
/// rebuild had already happened, which is one step too late to be the thing
/// that asks for it.</para>
///
/// <para>Not a git object either - <c>git rev-parse HEAD:scripts/hatch.sh</c>
/// answers for what is committed, and the files on disk are what runs. A
/// checkout with a local edit on it gives the right answer from the disk and
/// only sometimes from the index.</para>
/// </remarks>
public sealed record SelfPrint(IReadOnlyDictionary<string, string> Files, string Digest)
{
    /// <summary>Nothing read yet, which matches no tree at all.</summary>
    public static readonly SelfPrint Nothing = new(new Dictionary<string, string>(StringComparer.Ordinal), "");

    /// <summary>
    /// The paths that were added, removed or altered since <paramref name="older"/>,
    /// sorted - which is what a restart prints to say why it is restarting.
    /// </summary>
    public IReadOnlyList<string> ChangedFrom(SelfPrint older)
    {
        var changed = new List<string>();

        foreach (var (path, hash) in Files)
            if (!older.Files.TryGetValue(path, out var was) || was != hash)
                changed.Add(path);

        foreach (var path in older.Files.Keys)
            if (!Files.ContainsKey(path))
                changed.Add(path);

        changed.Sort(StringComparer.Ordinal);
        return changed;
    }
}

/// <summary>
/// Reading the loop's own source, behind an interface for the same reason
/// <see cref="IWorkspace"/> is one: a test asserts what the loop decides when
/// its source changes underneath it, without a git tree to change.
/// </summary>
public interface ISelf
{
    SelfPrint Take();
}

/// <summary>The real one, over the files this program is compiled from.</summary>
/// <remarks>
/// <para>Three places, and each is in the set for a reason.
/// <c>src/Aerie.Hatch</c> is the loop. <c>src/Aerie.Hatch.Contracts</c> is what
/// it is compiled against, so a wire record that changed is a runner that has
/// to be rebuilt. And <c>scripts/hatch.sh</c> is in it because it resolves the
/// runner and reads the settings - a night that improved the script is a night
/// running the old one until somebody stops it, which is the thing being built
/// away from.</para>
///
/// <para>Paths are recorded repo-relative with forward slashes and sorted
/// ordinally before hashing, so the digest is the same number on either
/// platform rather than something a Windows operator gets a different answer
/// for.</para>
/// </remarks>
public sealed class LoopSource(string root) : ISelf
{
    /// <summary>What the loop is made of, repo-relative.</summary>
    internal static readonly string[] Paths =
    [
        "scripts/hatch.sh",
        "src/Aerie.Hatch",
        "src/Aerie.Hatch.Contracts",
    ];

    public SelfPrint Take()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var relative in Paths)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(full))
            {
                Add(relative, full);
                continue;
            }

            if (!Directory.Exists(full)) continue;

            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var under = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

                // Build output is not source: it changes on every compile, and a
                // loop that restarted because it had just been built would never
                // do anything else.
                if (Built(under)) continue;

                Add(under, file);
            }
        }

        var whole = new StringBuilder();
        foreach (var (path, hash) in files) whole.Append(path).Append('\0').Append(hash).Append('\n');

        return new SelfPrint(files, Hash(Encoding.UTF8.GetBytes(whole.ToString())));

        void Add(string path, string full)
        {
            try
            {
                files[path] = Hash(File.ReadAllBytes(full));
            }
            catch (IOException)
            {
                // A file being written as it is read - a checkout mid-reset, an
                // editor saving. It reads as absent this time and is read again
                // next pass; a restart on the pass after is the right answer and
                // a crash is not.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    private static bool Hidden(string segment) => segment is "bin" or "obj";

    private static bool Built(string path) => path.Split('/').Any(Hidden);

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

/// <summary>
/// What one incarnation of the loop hands to the next: everything a bound is
/// measured against, so a restart cannot be a way of starting the night over.
/// </summary>
/// <remarks>
/// Written by the outgoing process and read by the incoming one, at a path the
/// supervisor names once per night in <c>HATCH_NIGHT_STATE</c>. It is not one of
/// <see cref="Settings.FileNames"/>: an operator never sets it, and a stale one
/// cannot be read by a later night because the path belongs to the supervisor
/// that made it and goes with it.
/// </remarks>
public sealed record NightState
{
    /// <summary>Increments run so far, across every incarnation.</summary>
    public int Runs { get; init; }

    /// <summary>What they cost, in dollars.</summary>
    public decimal Spent { get; init; }

    /// <summary>When the first incarnation started, so the elapsed time is the night's.</summary>
    public DateTimeOffset Started { get; init; }

    /// <summary>The failure streak as it stood, so a restart is not a way to clear it.</summary>
    public int Fails { get; init; }

    /// <summary>How many times the loop has come back so far.</summary>
    public int Restarts { get; init; }

    /// <summary>
    /// The instant <c>--until</c> resolved to when it was typed. Carried rather
    /// than recomputed: <c>--until 23:59</c> typed at 23:58 and re-read at 00:01
    /// resolves to tomorrow, and extends the night by a day.
    /// </summary>
    public DateTimeOffset? UntilAt { get; init; }

    /// <summary>The tally's two lists, so the morning's report is the whole night's.</summary>
    public IReadOnlyList<string> Moved { get; init; } = [];

    public IReadOnlyList<string> Stalled { get; init; } = [];

    /// <summary>
    /// What is at <paramref name="path"/>, or null - which is a fresh night.
    /// A file that is missing, unreadable or malformed reads as null rather than
    /// throwing: the worst a lost state file may cost is a night's bookkeeping,
    /// and a loop that would not start because of one is worse.
    /// </summary>
    public static NightState? Read(string? path)
    {
        if (path is not { Length: > 0 } || !File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<NightState>(File.ReadAllText(path), HatchClient.Json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Hand it forward. Answers whether it got there.</summary>
    public bool Write(string? path)
    {
        if (path is not { Length: > 0 }) return false;

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, HatchClient.Json));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Take it away, on every ending that is not a restart - so that the file on
    /// disk means "a loop is coming back" and never "a loop once ran here".
    /// </summary>
    public static void Forget(string? path)
    {
        if (path is not { Length: > 0 }) return;

        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The supervisor's trap removes it too. Two owners, and neither has
            // to succeed for the other to.
        }
    }
}
