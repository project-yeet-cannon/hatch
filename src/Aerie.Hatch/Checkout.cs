using System.Security.Cryptography;
using System.Text;

namespace Aerie.Hatch;

/// <summary>
/// Which tree this runner is working in, and what to call it.
///
/// Both questions used to have one answer - "here" - and stopped having one the
/// moment two loops were allowed to run on one machine. A checkout is now an
/// identity: the thing the lock is taken on, and the thing the board names when
/// it says who is holding a ticket.
/// </summary>
public static class Checkout
{
    /// <summary>
    /// The repository the increment happens in: the directory given, or the
    /// nearest ancestor of the working directory carrying a <c>.git</c>.
    /// </summary>
    /// <remarks>
    /// A session is spawned here because a ticket is about this codebase, and
    /// one started somewhere else would have to be told so.
    /// </remarks>
    public static string? Find(string? given, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(given))
            return Directory.Exists(given) ? Path.GetFullPath(given) : null;

        var dir = new DirectoryInfo(workingDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// One directory, spelled one way - so that two spellings of one checkout
    /// cannot take two locks on it.
    /// </summary>
    /// <remarks>
    /// <para>On the machine this was written on, <c>/Users/x/code/Aerie</c> and
    /// <c>/Users/x/code/aerie</c> are one directory: one inode, two spellings,
    /// because the filesystem is case-insensitive. A lock keyed on the string
    /// would let two loops into one tree, which is the exact thing the lock
    /// exists to prevent.</para>
    ///
    /// <para>Each segment is resolved to the name the directory it sits in
    /// actually holds, and symlinks are followed first. That is the same answer
    /// device-and-inode would give on Unix, and unlike device-and-inode it is a
    /// question Windows can also answer - which matters, because being
    /// cross-platform is half of why this stopped being a shell script. Where a
    /// filesystem <em>is</em> case-sensitive, both spellings exist as separate
    /// entries and both survive this untouched, which is the right answer
    /// there.</para>
    /// </remarks>
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);

        // Through a symlinked checkout to the tree it points at, since a lock
        // on the link and a lock on the target would be two locks on one tree.
        try
        {
            if (Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } target)
                full = Path.GetFullPath(target.FullName);
        }
        catch (IOException)
        {
            // Not a link, or not readable as one. The path as given is the answer.
        }

        var root = Path.GetPathRoot(full) ?? "";
        var segments = full[root.Length..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0)
            .ToArray();

        var walked = root;
        foreach (var segment in segments)
        {
            var next = Path.Combine(walked, segment);
            try
            {
                // The entry as the parent directory actually spells it. An
                // ordinal match first, so a case-sensitive filesystem holding
                // both spellings keeps the one that was asked for.
                var siblings = Directory.GetFileSystemEntries(walked);
                var match =
                    siblings.FirstOrDefault(s => string.Equals(Path.GetFileName(s), segment, StringComparison.Ordinal))
                    ?? siblings.FirstOrDefault(s =>
                        string.Equals(Path.GetFileName(s), segment, StringComparison.OrdinalIgnoreCase));

                walked = match is null ? next : Path.Combine(walked, Path.GetFileName(match));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A directory that cannot be listed cannot be canonicalised.
                // The path as given is still an identity, just a weaker one.
                walked = next;
            }
        }

        return walked;
    }

    /// <summary>
    /// The checkout as a name a lock file can wear: twelve hex characters of a
    /// digest over <see cref="Canonical"/>.
    /// </summary>
    /// <remarks>
    /// A digest and not the path, because a path has separators and spaces in
    /// it and a filename should have neither. Twelve characters because this
    /// distinguishes the two or three checkouts on one developer's machine, not
    /// the contents of a content-addressed store.
    /// </remarks>
    public static string Fingerprint(string path)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(path)));
        return Convert.ToHexStringLower(digest)[..12];
    }

    /// <summary>
    /// Who the board says is holding a claim: the short hostname and the
    /// checkout, because it is read by a person deciding which box to go and
    /// look at. <c>HATCH_RUNNER</c> overrides it for a checkout whose path says
    /// nothing useful.
    /// </summary>
    /// <remarks>
    /// The path here is the one that was typed, not <see cref="Canonical"/>'s -
    /// a lock is a machine's identity and a runner is a sentence, and the
    /// sentence should say where somebody would go looking.
    /// </remarks>
    public static string Runner(string? configured, string host, string root)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Cap(configured.Trim());
        return Cap($"{host}:{root}");
    }

    /// <summary>
    /// The server refuses a runner over
    /// <see cref="ClaimRequest.MaxRunnerLength"/>, and a refused claim is a
    /// loop that cannot start - so a long one loses its head rather than its
    /// tail. The end of a path is the part that names a checkout.
    /// </summary>
    /// <remarks>
    /// The limit is read off the contract rather than written down again here.
    /// Two copies of a number the server enforces is one copy too many, and the
    /// one that would be wrong is always the client's.
    /// </remarks>
    private static string Cap(string runner) =>
        runner.Length <= ClaimRequest.MaxRunnerLength
            ? runner
            : "..." + runner[^(ClaimRequest.MaxRunnerLength - 3)..];
}
