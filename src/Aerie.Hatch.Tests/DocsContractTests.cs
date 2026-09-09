namespace Aerie.Hatch.Tests;

/// <summary>
/// The block a friend pastes into their own repository's <c>CLAUDE.md</c> tells
/// their sessions which commands to type. Nothing else keeps it honest: the doc
/// is prose in another directory, and a renamed verb would leave it saying the
/// old name until somebody's overnight run failed on it.
///
/// So the doc is read here, against <see cref="Program.Commands"/> - the one
/// list the dispatch itself is built from - and a rename fails the build
/// instead.
/// </summary>
public sealed class DocsContractTests
{
    private const string Start = "<!-- claude-contract:start -->";
    private const string End = "<!-- claude-contract:end -->";

    [Fact]
    public void Every_command_the_cli_answers_to_is_named_in_the_block()
    {
        var block = ContractBlock();

        // Word-bounded rather than a bare Contains: "work" is a substring of
        // "go-to-work", and a doc that had lost the short one would still pass.
        var missing = Program.Commands.Where(command => !Names(block, command)).ToArray();

        Assert.True(missing.Length == 0,
            $"docs/hatch-at-home.md's CLAUDE.md block does not name: {string.Join(", ", missing)}. " +
            "Either the command was renamed and the doc was not, or the block was edited down.");
    }

    /// <summary>
    /// The markers are what makes the assertion above about the block rather
    /// than about the file - prose elsewhere in the doc names commands too, and
    /// a whole-file search would pass on a doc whose block had been deleted.
    /// </summary>
    [Fact]
    public void The_block_is_marked_off_from_the_prose_around_it()
    {
        var doc = File.ReadAllText(Doc());

        Assert.Equal(1, Occurrences(doc, Start));
        Assert.Equal(1, Occurrences(doc, End));
        Assert.True(doc.IndexOf(Start, StringComparison.Ordinal) < doc.IndexOf(End, StringComparison.Ordinal),
            "The end marker comes before the start marker.");
    }

    /// <summary>
    /// The whole point of the block is that it travels: it is pasted into a
    /// repository that is not this one, where every path here is absent. So no
    /// mention of this repository's own wrapper, and no relative link.
    /// </summary>
    [Fact]
    public void Nothing_in_the_block_points_at_a_path_only_this_repository_has()
    {
        var block = ContractBlock();

        Assert.DoesNotContain("scripts/hatch.sh", block, StringComparison.Ordinal);
        Assert.DoesNotContain("](", block, StringComparison.Ordinal);
    }

    private static string ContractBlock()
    {
        var doc = File.ReadAllText(Doc());

        var from = doc.IndexOf(Start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"docs/hatch-at-home.md has no {Start} marker.");
        from += Start.Length;

        var to = doc.IndexOf(End, from, StringComparison.Ordinal);
        Assert.True(to >= 0, $"docs/hatch-at-home.md has no {End} marker after the start one.");

        return doc[from..to];
    }

    private static int Occurrences(string text, string marker)
    {
        var count = 0;
        for (var at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// True when <paramref name="command"/> appears in <paramref name="text"/>
    /// as a whole word - neither side touching a letter, a digit or a hyphen.
    /// The hyphen is what separates <c>work</c> from <c>go-to-work</c>.
    /// </summary>
    private static bool Names(string text, string command)
    {
        for (var at = text.IndexOf(command, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(command, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !Part(text[at - 1]);
            var afterAt = at + command.Length;
            var after = afterAt == text.Length || !Part(text[afterAt]);
            if (before && after) return true;
        }

        return false;

        static bool Part(char c) => char.IsLetterOrDigit(c) || c == '-';
    }

    /// <summary>
    /// Walks up from the test binary to the repository, the way
    /// <c>GameEngineReferenceTests</c> does in the API's suite, rather than
    /// assuming how deep <c>bin/Debug/net10.0</c> happens to be today.
    /// </summary>
    private static string Doc()
    {
        const string relative = "docs/hatch-at-home.md";

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not find {relative} above {AppContext.BaseDirectory}.");
    }
}
