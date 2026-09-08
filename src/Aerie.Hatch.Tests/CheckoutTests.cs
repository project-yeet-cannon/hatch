namespace Aerie.Hatch.Tests;

/// <summary>
/// One loop per checkout, and two loops in two checkouts - which is the line
/// that lets a second one start at all.
/// </summary>
public sealed class CheckoutTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("hatch-lock-").FullName;

    private string Tree(string name)
    {
        var path = Path.Combine(_temp, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Two_checkouts_on_one_machine_both_run()
    {
        var first = LoopLock.Take(Tree("one"), _temp, out _);
        var second = LoopLock.Take(Tree("two"), _temp, out var refusal);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("", refusal);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void Two_loops_in_one_checkout_do_not()
    {
        var tree = Tree("one");
        using var held = LoopLock.Take(tree, _temp, out _);
        var second = LoopLock.Take(tree, _temp, out var refusal);

        Assert.NotNull(held);
        Assert.Null(second);

        // The pid, so the second loop can go and find the first, and the
        // checkout, because "already running here" is ambiguous the moment there
        // are two heres.
        Assert.Contains($"pid {Environment.ProcessId}", refusal, StringComparison.Ordinal);
        Assert.Contains(tree, refusal, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Two_spellings_of_one_path_are_one_checkout()
    {
        var tree = Tree("Aerie");
        var lowered = Path.Combine(_temp, "aerie");

        // Only where the filesystem folds case, which is a property of the disk
        // and not of this code. Where it does not, the two spellings really are
        // two directories and keeping them apart is the right answer.
        Skip.IfNot(Directory.Exists(lowered), "this filesystem is case-sensitive, so there is one spelling");

        Assert.Equal(Checkout.Fingerprint(tree), Checkout.Fingerprint(lowered));

        using var held = LoopLock.Take(tree, _temp, out _);
        Assert.Null(LoopLock.Take(lowered, _temp, out var refusal));
        Assert.Contains("already running", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_written_the_long_way_round_is_the_same_checkout()
    {
        var tree = Tree("one");
        var roundabout = Path.Combine(_temp, "two", "..", "one");
        Directory.CreateDirectory(Path.Combine(_temp, "two"));

        Assert.Equal(Checkout.Fingerprint(tree), Checkout.Fingerprint(roundabout));
    }

    [SkippableFact]
    public void A_symlinked_checkout_is_the_tree_it_points_at()
    {
        var tree = Tree("one");
        var link = Path.Combine(_temp, "link");

        try
        {
            Directory.CreateSymbolicLink(link, tree);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Skip.If(true, "this platform will not make a symlink without a privilege");
        }

        Assert.Equal(Checkout.Fingerprint(tree), Checkout.Fingerprint(link));
    }

    [Fact]
    public void A_lock_whose_owner_is_gone_is_cleared_and_taken()
    {
        var tree = Tree("one");
        using var abandoned = LoopLock.Take(tree, _temp, out _);
        Assert.NotNull(abandoned);

        // The pid file is still there and the process behind it is not - a
        // machine that rebooted, or a kill -9. That is litter, not a lock.
        using var taken = LoopLock.Take(tree, _temp, out var refusal, alive: _ => false);

        Assert.NotNull(taken);
        Assert.Equal("", refusal);
    }

    [Fact]
    public void A_released_lock_is_taken_again()
    {
        var tree = Tree("one");
        LoopLock.Take(tree, _temp, out _)!.Dispose();

        using var again = LoopLock.Take(tree, _temp, out _);
        Assert.NotNull(again);
    }

    // ---- What the board calls this runner ----

    [Fact]
    public void A_runner_is_the_host_and_the_checkout()
    {
        Assert.Equal("kestrel:/Users/x/code/Aerie", Checkout.Runner(null, "kestrel", "/Users/x/code/Aerie"));
    }

    [Fact]
    public void HATCH_RUNNER_wins()
    {
        Assert.Equal("the-box", Checkout.Runner("the-box", "kestrel", "/Users/x/code/Aerie"));
    }

    [Fact]
    public void A_long_runner_loses_its_head_rather_than_its_tail()
    {
        var deep = "/" + string.Join('/', Enumerable.Repeat("a-directory-with-a-long-name", 20)) + "/Aerie";
        var runner = Checkout.Runner(null, "kestrel", deep);

        // The server refuses a longer one, and a refused claim is a loop that
        // cannot start.
        Assert.Equal(ClaimRequest.MaxRunnerLength, runner.Length);

        // The end of a path is the part that names a checkout, so that is the
        // part that survives.
        Assert.StartsWith("...", runner, StringComparison.Ordinal);
        Assert.EndsWith("/Aerie", runner, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // The operating system's to tidy.
        }
    }
}
