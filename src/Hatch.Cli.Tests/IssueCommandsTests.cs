using System.Net;

namespace Hatch.Cli.Tests;

/// <summary>
/// <c>show</c>, <c>start</c>, <c>move</c>, <c>comment</c> and <c>pr</c> - the
/// five things a working session does to one ticket.
/// </summary>
public sealed class IssueCommandsTests
{
    private static BoardDto ABoard() =>
        new(
            [
                Fixtures.Status(2, "To Do"),
                Fixtures.Status(3, "In Progress"),
                Fixtures.Status(4, "Done", terminal: true),
                Fixtures.Status(5, "Shelved", deferred: true),
            ],
            []);

    private static IssueDto AnIssue(
        string key = "AER-12",
        string? parent = null,
        IReadOnlyList<string>? children = null,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? dependents = null,
        string? readyAt = null,
        string? dueAt = null,
        string? pr = null) =>
        Fixtures.Issue(key) with
        {
            ParentKey = parent,
            ChildKeys = children ?? [],
            DependsOnKeys = dependsOn ?? [],
            DependentKeys = dependents ?? [],
            ReadyAt = readyAt,
            DueAt = dueAt,
            PullRequestUrl = pr,
        };

    // ---- show ----

    [Fact]
    public async Task Show_prints_the_header_the_edges_it_has_and_the_brief()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue(
            parent: "AER-1", children: ["AER-13"], dependsOn: ["AER-11"], dependents: ["AER-14"],
            readyAt: "2026-10-01", dueAt: "2026-10-08"));
        h.Wire.Json("GET", "/api/hatch/statuses", new[] { Fixtures.Status(3, "In Progress") });
        h.Wire.Json("GET", "/api/hatch/issues/AER-12/comments", Array.Empty<CommentDto>());

        Assert.Equal(0, await new IssueCommands(h.Cli).ShowAsync(["AER-12"], default));

        Assert.Equal(
            """
            AER-12  [task]  A ticket
            status:   In Progress
            parent:   AER-1
            children: AER-13
            depends:  AER-11
            blocks:   AER-14
            ready:    2026-10-01
            due:      2026-10-08

            The brief.

            """.ReplaceLineEndings("\n"),
            h.Said);
    }

    /// <summary>An edge an issue does not have is not a line saying it has none.</summary>
    [Fact]
    public async Task Show_leaves_out_every_line_the_issue_has_nothing_for()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue());
        h.Wire.Json("GET", "/api/hatch/statuses", new[] { Fixtures.Status(3, "In Progress") });
        h.Wire.Json("GET", "/api/hatch/issues/AER-12/comments", Array.Empty<CommentDto>());

        await new IssueCommands(h.Cli).ShowAsync(["AER-12"], default);

        foreach (var absent in new[] { "parent:", "children:", "depends:", "blocks:", "ready:", "due:", "expedite:" })
            Assert.DoesNotContain(absent, h.Said);
    }

    [Fact]
    public async Task Show_says_when_the_issue_is_expedited()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue() with { Expedited = true });
        h.Wire.Json("GET", "/api/hatch/statuses", new[] { Fixtures.Status(3, "In Progress") });
        h.Wire.Json("GET", "/api/hatch/issues/AER-12/comments", Array.Empty<CommentDto>());

        Assert.Equal(0, await new IssueCommands(h.Cli).ShowAsync(["AER-12"], default));

        Assert.Contains("expedite: yes - this one goes first", h.Said);
    }

    /// <summary>
    /// The terminal reads the flag and writes it nowhere: the CLI authenticates
    /// with a key, and this is a person's write (docs/hatch.md, "The one edge
    /// that is deliberately cut"). An omission somebody would otherwise file a
    /// bug about, so it is a test.
    /// </summary>
    [Fact]
    public void There_is_no_expedite_verb()
    {
        Assert.DoesNotContain("expedite", Program.Commands, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Show_prints_the_comments_under_a_count_of_them()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue());
        h.Wire.Json("GET", "/api/hatch/statuses", new[] { Fixtures.Status(3, "In Progress") });
        h.Wire.Json("GET", "/api/hatch/issues/AER-12/comments", new[]
        {
            new CommentDto(1, "hatch", "First.", "", null, null, DateTimeOffset.UnixEpoch),
            new CommentDto(2, "somebody", "Second,\nover two lines.", "", null, null, DateTimeOffset.UnixEpoch),
        });

        await new IssueCommands(h.Cli).ShowAsync(["AER-12"], default);

        Assert.Contains("--- 2 comment(s) ---", h.Said);
        Assert.Contains("[1970-01-01T00:00:00+00:00] hatch:", h.Said);
        Assert.Contains("Second,\nover two lines.", h.Said);
    }

    /// <summary>
    /// The status name, not the id: a number is a fact about the database and
    /// the column's name is the fact the reader wanted.
    /// </summary>
    [Fact]
    public async Task Show_falls_back_to_the_status_id_when_no_column_matches_it()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue());
        h.Wire.Json("GET", "/api/hatch/statuses", Array.Empty<StatusDto>());
        h.Wire.Json("GET", "/api/hatch/issues/AER-12/comments", Array.Empty<CommentDto>());

        await new IssueCommands(h.Cli).ShowAsync(["AER-12"], default);
        Assert.Contains("status:   3", h.Said);
    }

    // ---- start and move ----

    [Fact]
    public async Task Start_is_move_with_the_column_already_named()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard());
        h.Wire.Json("POST", "/api/hatch/issues/AER-12/move", AnIssue());

        Assert.Equal(0, await new IssueCommands(h.Cli).StartAsync(["AER-12"], default));

        Assert.Equal(3, h.Wire.To("POST", "/api/hatch/issues/AER-12/move").Single().Read<IssueMoveRequest>().StatusId);
        Assert.Equal("AER-12 -> in progress", h.Said);
    }

    [Fact]
    public async Task Move_finds_the_column_however_it_was_typed_and_says_where_it_went()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard());
        h.Wire.Json("POST", "/api/hatch/issues/AER-12/move", AnIssue());

        Assert.Equal(0, await new IssueCommands(h.Cli).MoveAsync(["AER-12", "TODO"], default));

        Assert.Equal(2, h.Wire.To("POST", "/api/hatch/issues/AER-12/move").Single().Read<IssueMoveRequest>().StatusId);
        Assert.Equal("AER-12 -> TODO", h.Said);
    }

    /// <summary>
    /// Refused in the tool rather than left to a careful prompt, because the
    /// rule is about the tool and not about who is holding it.
    /// </summary>
    [Fact]
    public async Task A_terminal_column_is_refused_and_nothing_is_written()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard());

        Assert.Equal(1, await new IssueCommands(h.Cli).MoveAsync(["AER-12", "done"], default));

        Assert.Contains("is a terminal column - only the operator moves a ticket there", h.Complained);
        Assert.Empty(h.Wire.To("POST", "/api/hatch/issues/AER-12/move"));
    }

    /// <summary>
    /// And a deferred column on the same footing: shelving a ticket is a
    /// decision about whether the work is worth doing, which is the operator's
    /// to make and not a session's to make on the way past.
    /// </summary>
    [Fact]
    public async Task A_deferred_column_is_refused_and_nothing_is_written()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard());

        Assert.Equal(1, await new IssueCommands(h.Cli).MoveAsync(["AER-12", "shelved"], default));

        Assert.Contains("is a deferred column - only the operator shelves a ticket", h.Complained);
        Assert.Empty(h.Wire.To("POST", "/api/hatch/issues/AER-12/move"));
    }

    [Fact]
    public async Task Moving_to_a_column_nobody_has_names_the_ones_there_are()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard());

        Assert.Equal(1, await new IssueCommands(h.Cli).MoveAsync(["AER-12", "elsewhere"], default));
        Assert.Contains("no column called \"elsewhere\" - there is To Do, In Progress, Done, Shelved", h.Complained);
    }

    // ---- comment ----

    [Fact]
    public async Task A_comment_is_posted_whole_and_the_author_is_read_back()
    {
        using var h = new CliHarness();
        h.Wire.Json("POST", "/api/hatch/issues/AER-12/comments",
            new CommentDto(7, "hatch", "sha abc123", "", null, null, DateTimeOffset.UnixEpoch));

        Assert.Equal(0, await new IssueCommands(h.Cli).CommentAsync(["AER-12", "sha abc123"], default));

        var sent = h.Wire.To("POST", "/api/hatch/issues/AER-12/comments").Single().Read<CommentCreateRequest>();
        Assert.Equal("sha abc123", sent.Body);
        Assert.Null(sent.Kind);
        Assert.Equal("commented on AER-12 as hatch", h.Said);
    }

    // ---- pr ----

    /// <summary>
    /// The URL alone, so <c>open "$(hatch pr AER-12)"</c> is the whole of "show
    /// me the review".
    /// </summary>
    [Fact]
    public async Task Pr_with_no_url_reads_the_one_that_is_set_and_prints_nothing_else()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue(pr: "https://example.invalid/pull/1"));

        Assert.Equal(0, await new IssueCommands(h.Cli).PrAsync(["AER-12"], default));
        Assert.Equal("https://example.invalid/pull/1", h.Said);
    }

    [Fact]
    public async Task Pr_on_an_issue_pointing_at_nothing_is_two_rather_than_one()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12", AnIssue());

        Assert.Equal(2, await new IssueCommands(h.Cli).PrAsync(["AER-12"], default));
        Assert.Contains("AER-12 points at no pull request", h.Complained);
    }

    [Fact]
    public async Task Pr_with_a_url_patches_the_issue_and_reads_it_back()
    {
        using var h = new CliHarness();
        h.Wire.Json("PATCH", "/api/hatch/issues/AER-12", AnIssue(pr: "https://example.invalid/pull/2"));

        Assert.Equal(0, await new IssueCommands(h.Cli).PrAsync(["AER-12", "https://example.invalid/pull/2"], default));

        var sent = h.Wire.To("PATCH", "/api/hatch/issues/AER-12").Single().Read<IssuePatchRequest>();
        Assert.Equal("https://example.invalid/pull/2", sent.PullRequestUrl);
        Assert.Null(sent.Title);
        Assert.Equal("https://example.invalid/pull/2", h.Said);
    }

    [Fact]
    public async Task Clear_is_spelled_out_loud_and_sends_the_empty_string_the_api_reads_as_one()
    {
        using var h = new CliHarness();
        h.Wire.Json("PATCH", "/api/hatch/issues/AER-12", AnIssue());

        Assert.Equal(0, await new IssueCommands(h.Cli).PrAsync(["AER-12", "--clear"], default));

        Assert.Equal("", h.Wire.To("PATCH", "/api/hatch/issues/AER-12").Single().Read<IssuePatchRequest>().PullRequestUrl);
        Assert.Equal("AER-12 points at no pull request", h.Said);
    }

    /// <summary>
    /// An empty argument at a prompt is easy to pass by accident and impossible
    /// to see afterwards, so it is the mistake rather than a silent clear.
    /// </summary>
    [Fact]
    public async Task An_empty_url_is_refused_rather_than_taken_as_the_clear()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new IssueCommands(h.Cli).PrAsync(["AER-12", ""], default));
        Assert.Contains("give a url, or --clear to take AER-12 off the one it has", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    // ---- the shape they share ----

    [Fact]
    public async Task A_missing_issue_is_the_404_the_server_wrote()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/issues/AER-99", HttpStatusCode.NotFound);

        var thrown = await Assert.ThrowsAsync<HatchException>(
            () => new IssueCommands(h.Cli).ShowAsync(["AER-99"], default));

        Assert.Contains("404 - no such issue or route", thrown.Message);
    }

    [Theory]
    [InlineData("show", 0)]
    [InlineData("start", 0)]
    [InlineData("move", 1)]
    [InlineData("comment", 1)]
    [InlineData("pr", 0)]
    public async Task Each_of_them_prints_its_own_usage_for_dash_h(string command, int _)
    {
        using var h = new CliHarness();
        var commands = new IssueCommands(h.Cli);

        var code = command switch
        {
            "show" => await commands.ShowAsync(["-h"], default),
            "start" => await commands.StartAsync(["--help"], default),
            "move" => await commands.MoveAsync(["-h"], default),
            "comment" => await commands.CommentAsync(["-h"], default),
            _ => await commands.PrAsync(["--help"], default),
        };

        Assert.Equal(0, code);
        Assert.StartsWith($"usage: hatch {command}", h.Said);
        Assert.Empty(h.Wire.Calls);
    }

    /// <summary>
    /// A body that happens to read <c>--help</c> is a body somebody meant to
    /// post, which is why the usage check is the first argument only.
    /// </summary>
    [Fact]
    public async Task A_comment_body_that_looks_like_a_flag_is_still_a_comment()
    {
        using var h = new CliHarness();
        h.Wire.Json("POST", "/api/hatch/issues/AER-12/comments",
            new CommentDto(1, "hatch", "--help", "", null, null, DateTimeOffset.UnixEpoch));

        Assert.Equal(0, await new IssueCommands(h.Cli).CommentAsync(["AER-12", "--help"], default));
        Assert.Equal("--help", h.Wire.To("POST", "/api/hatch/issues/AER-12/comments").Single()
            .Read<CommentCreateRequest>().Body);
    }
}
