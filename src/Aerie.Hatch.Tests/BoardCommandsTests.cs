using System.Net;

namespace Aerie.Hatch.Tests;

/// <summary>
/// <c>board</c>, <c>next</c> and <c>queue</c> - the three reads of the board
/// itself, and the two rules they share: a column is found by its name on the
/// letters and digits alone, and a card whose ready date has not arrived is
/// folded past.
/// </summary>
public sealed class BoardCommandsTests
{
    private static BoardDto ABoard(params IssueCardDto[] cards) =>
        new(
            [
                Fixtures.Status(1, "Backlog"),
                Fixtures.Status(2, "To Do"),
                Fixtures.Status(3, "In Progress"),
                Fixtures.Status(4, "Done", terminal: true),
            ],
            cards);

    private static IssueCardDto Card(
        string key, int statusId, string type = "task", string? readyAt = null, string? dueAt = null) =>
        new(key, "AER", type, $"{key}'s title", statusId, 1000, null, readyAt, dueAt);

    [Fact]
    public async Task The_board_is_every_column_and_what_is_on_it_with_the_terminal_one_marked()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard(Card("AER-1", 2), Card("AER-2", 2), Card("AER-3", 3)));

        Assert.Equal(0, await new BoardCommands(h.Cli).BoardAsync([], default));

        Assert.Equal(
            """
            Backlog: 0
            To Do: 2
            In Progress: 1
            Done (terminal): 0
            """.ReplaceLineEndings("\n"),
            h.Said);
    }

    /// <summary>
    /// The whole point of matching on letters and digits: statuses are rows and
    /// the operator renames them, so "todo" has to reach a column called
    /// "To Do" without anybody editing a script.
    /// </summary>
    [Theory]
    [InlineData("todo")]
    [InlineData("To Do")]
    [InlineData("TODO")]
    [InlineData("to-do")]
    [InlineData("  to do  ")]
    public async Task A_column_is_found_by_its_letters_and_digits_however_it_is_typed(string typed)
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard(Card("AER-1", 2)));

        Assert.Equal(0, await new BoardCommands(h.Cli).NextAsync([typed], default));
        Assert.Contains("AER-1", h.Said);
    }

    [Fact]
    public async Task Next_with_no_column_named_means_todo()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard(Card("AER-9", 3), Card("AER-1", 2)));

        Assert.Equal(0, await new BoardCommands(h.Cli).NextAsync([], default));
        Assert.Equal("AER-1  [task]  AER-1's title", h.Said);
    }

    [Fact]
    public async Task A_due_date_is_printed_beside_the_card()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard(Card("AER-1", 2, dueAt: "2026-10-01")));

        await new BoardCommands(h.Cli).NextAsync([], default);
        Assert.Equal("AER-1  [task]  AER-1's title  (due 2026-10-01)", h.Said);
    }

    [Fact]
    public async Task A_column_nobody_has_is_refused_by_naming_the_ones_there_are()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard());

        Assert.Equal(1, await new BoardCommands(h.Cli).NextAsync(["nonsense"], default));
        Assert.Contains("no column called \"nonsense\"", h.Complained);
        Assert.Contains("Backlog, To Do, In Progress, Done", h.Complained);
    }

    /// <summary>
    /// Two exit codes, because they are two different answers. A column that
    /// does not exist is a mistake; a column with nothing workable in it is the
    /// board saying so, and a shell asking "is there anything" wants to tell
    /// them apart.
    /// </summary>
    [Fact]
    public async Task An_empty_column_is_two_rather_than_one()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/board", ABoard(Card("AER-1", 3)));

        Assert.Equal(2, await new BoardCommands(h.Cli).NextAsync(["todo"], default));
        Assert.Contains("nothing workable in \"todo\"", h.Complained);
    }

    /// <summary>
    /// A ready date is a question about calendar days in the reader's zone, and
    /// not about elapsed hours: an issue is workable from the start of the day
    /// it names, whatever hour it was set to.
    /// </summary>
    [Fact]
    public async Task A_card_whose_ready_date_has_not_arrived_is_folded_past()
    {
        var clock = new FrozenClock(new DateTimeOffset(2026, 9, 8, 11, 0, 0, TimeSpan.FromHours(-5)));
        using var h = new CliHarness(clock: clock);

        h.Wire.Json("GET", "/api/hatch/board", ABoard(
            Card("AER-1", 2, readyAt: "2026-09-09"),
            Card("AER-2", 2, readyAt: "2026-09-08"),
            Card("AER-3", 2)));

        await new BoardCommands(h.Cli).NextAsync([], default);
        Assert.Equal("AER-2  [task]  AER-2's title", h.Said);
    }

    [Fact]
    public async Task A_ready_date_that_is_today_in_the_readers_zone_is_ready_whatever_hour_it_names()
    {
        // Late in the evening, five hours behind UTC: the same instant is
        // already tomorrow in UTC, and the card is still today's here.
        var clock = new FrozenClock(new DateTimeOffset(2026, 9, 8, 22, 0, 0, TimeSpan.FromHours(-5)));
        using var h = new CliHarness(clock: clock);

        h.Wire.Json("GET", "/api/hatch/board", ABoard(Card("AER-1", 2, readyAt: "2026-09-08T23:30:00-05:00")));

        Assert.Equal(0, await new BoardCommands(h.Cli).NextAsync([], default));
        Assert.Contains("AER-1", h.Said);
    }

    /// <summary>
    /// "There is nothing" and "something went wrong and printed nothing" look
    /// identical as a blank line, which is the one thing a run nobody watched
    /// cannot afford to be unsure about.
    /// </summary>
    [Fact]
    public async Task An_empty_queue_is_a_sentence_and_not_a_blank_line()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/work/queue", Array.Empty<QueueEntryDto>());

        Assert.Equal(0, await new BoardCommands(h.Cli).QueueAsync([], default));
        Assert.Equal("hatch: nothing on the board is on the dispatcher's path", h.Said);
    }

    [Fact]
    public async Task An_empty_queue_under_an_epic_says_which_epic()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/work/queue", Array.Empty<QueueEntryDto>());

        await new BoardCommands(h.Cli).QueueAsync(["AER-1"], default);
        Assert.Equal("hatch: nothing under AER-1 is on the dispatcher's path", h.Said);
        Assert.Contains("ancestorKey=AER-1", h.Wire.To("GET", "/api/hatch/work/queue").Single().Query);
    }

    /// <summary>
    /// Padded to the widest value in the answer rather than to a guessed width,
    /// since status names are rows the operator renames.
    /// </summary>
    [Fact]
    public async Task The_queue_pads_its_columns_to_what_is_actually_in_them()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/work/queue", new[]
        {
            new QueueEntryDto(
                Fixtures.Issue("AER-1", type: "task"), Fixtures.Status(3, "In Progress"),
                Fixtures.Status(4, "In Review"), null),
            new QueueEntryDto(
                Fixtures.Issue("AER-1000", type: "story"), Fixtures.Status(2, "To Do"), null, "it waits on AER-1"),
        });

        await new BoardCommands(h.Cli).QueueAsync([], default);

        Assert.Equal(
            """
            AER-1     [task]   In Progress  -> In Review
            AER-1000  [story]  To Do        it waits on AER-1
            """.ReplaceLineEndings("\n"),
            h.Said);
    }

    /// <summary>A row with no next column still says something rather than nothing.</summary>
    [Fact]
    public async Task A_row_with_nowhere_to_go_prints_a_question_mark_rather_than_an_empty_arrow()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/work/queue", new[]
        {
            new QueueEntryDto(Fixtures.Issue("AER-1"), Fixtures.Status(3, "In Progress"), null, null),
        });

        await new BoardCommands(h.Cli).QueueAsync([], default);
        Assert.EndsWith("-> ?", h.Said);
    }

    [Fact]
    public async Task A_refusal_from_the_origin_is_the_sentence_the_origin_wrote()
    {
        using var h = new CliHarness();
        h.Wire.Reply("GET", "/api/hatch/board", HttpStatusCode.BadRequest, "\"AER-1 is in another project\"");

        var thrown = await Assert.ThrowsAsync<HatchException>(
            () => new BoardCommands(h.Cli).BoardAsync([], default));

        Assert.Contains("AER-1 is in another project", thrown.Message);
    }

    [Theory]
    [InlineData("board")]
    [InlineData("next")]
    [InlineData("queue")]
    public async Task Each_of_them_prints_its_own_usage_for_dash_h(string command)
    {
        using var h = new CliHarness();
        var commands = new BoardCommands(h.Cli);

        var code = command switch
        {
            "board" => await commands.BoardAsync(["-h"], default),
            "next" => await commands.NextAsync(["--help"], default),
            _ => await commands.QueueAsync(["-h"], default),
        };

        Assert.Equal(0, code);
        Assert.StartsWith($"usage: hatch {command}", h.Said);
        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task Too_many_arguments_is_the_mistake_and_then_the_usage_block()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new BoardCommands(h.Cli).NextAsync(["a", "b"], default));
        Assert.Contains("next takes one column", h.Complained);
        Assert.Contains("usage: hatch next", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }
}
