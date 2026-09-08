namespace Aerie.Hatch.Tests;

/// <summary>
/// <c>depends</c> - what an issue waits on, and what waits on it, read, added
/// and removed.
/// </summary>
public sealed class DependsCommandTests
{
    private static IssueDto AnIssue(
        string key = "AER-12", IReadOnlyList<string>? waitsOn = null, IReadOnlyList<string>? waitedOnBy = null) =>
        Fixtures.Issue(key) with { DependsOnKeys = waitsOn ?? [], DependentKeys = waitedOnBy ?? [] };

    /// <summary>
    /// Both directions, printed even when empty - "there is nothing" and
    /// "something went wrong and printed nothing" look identical otherwise.
    /// </summary>
    [Fact]
    public async Task An_issue_with_no_edges_says_so_in_both_directions()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue());

        Assert.Equal(0, await new DependsCommand(h.Cli).RunAsync(["AER-12"], default));

        Assert.Equal(
            """
            AER-12 waits on nothing
            nothing waits on AER-12
            """.ReplaceLineEndings("\n"),
            h.Said);
    }

    [Fact]
    public async Task One_dependent_waits_and_several_wait()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12",
            AnIssue(waitsOn: ["AER-11"], waitedOnBy: ["AER-13", "AER-14"]));

        await new DependsCommand(h.Cli).RunAsync(["AER-12"], default);

        Assert.Equal(
            """
            AER-12 waits on AER-11
            AER-13, AER-14 wait on AER-12
            """.ReplaceLineEndings("\n"),
            h.Said);
    }

    [Fact]
    public async Task A_single_dependent_is_singular()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue(waitedOnBy: ["AER-13"]));

        await new DependsCommand(h.Cli).RunAsync(["AER-12"], default);
        Assert.Contains("AER-13 waits on AER-12", h.Said);
    }

    [Fact]
    public async Task A_second_key_is_the_edge_to_add()
    {
        using var h = new CliHarness();
        h.Wire.Json("POST", "/api/hatch/issues/AER-13/dependencies", AnIssue("AER-13", waitsOn: ["AER-12"]));

        Assert.Equal(0, await new DependsCommand(h.Cli).RunAsync(["AER-13", "AER-12"], default));

        Assert.Equal("AER-12", h.Wire.To("POST", "/api/hatch/issues/AER-13/dependencies").Single()
            .Read<IssueDependencyRequest>().DependsOnKey);
        Assert.Contains("AER-13 waits on AER-12", h.Said);
    }

    [Fact]
    public async Task Remove_takes_the_edge_off_by_naming_it_in_the_path()
    {
        using var h = new CliHarness();
        h.Wire.Json("DELETE", "/api/hatch/issues/AER-13/dependencies/AER-12", AnIssue("AER-13"));

        Assert.Equal(0, await new DependsCommand(h.Cli).RunAsync(["AER-13", "--remove", "AER-12"], default));
        Assert.Contains("AER-13 waits on nothing", h.Said);
    }

    [Fact]
    public async Task Remove_with_nothing_to_remove_is_refused_and_writes_nothing()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new DependsCommand(h.Cli).RunAsync(["AER-13", "--remove"], default));
        Assert.Contains("say which issue AER-13 should stop waiting on", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    /// <summary>
    /// Distinguished by the argument count rather than by its value, so an empty
    /// one is a mistake and not a silent nothing - the same rule <c>pr</c>
    /// splits on.
    /// </summary>
    [Fact]
    public async Task An_empty_second_argument_is_the_mistake_and_not_a_read()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new DependsCommand(h.Cli).RunAsync(["AER-13", ""], default));
        Assert.Contains("say which issue AER-13 waits on, or --remove one", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task No_key_at_all_is_the_usage_block()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new DependsCommand(h.Cli).RunAsync([], default));
        Assert.Contains("depends takes an issue key", h.Complained);
        Assert.Contains("usage: hatch depends", h.Complained);
    }

    [Fact]
    public async Task Dash_h_prints_its_own_usage_and_calls_nothing()
    {
        using var h = new CliHarness();

        Assert.Equal(0, await new DependsCommand(h.Cli).RunAsync(["-h"], default));
        Assert.StartsWith("usage: hatch depends", h.Said);
        Assert.Empty(h.Wire.Calls);
    }
}
