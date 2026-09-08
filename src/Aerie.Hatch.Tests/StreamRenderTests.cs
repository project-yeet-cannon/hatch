namespace Aerie.Hatch.Tests;

/// <summary>
/// The CLI's events, read back: the two facts a caller needs out of a run, and
/// the block a session is asked to end with.
/// </summary>
public sealed class StreamRenderTests
{
    private static (RunFacts Facts, List<string> Lines) Play(params string[] events)
    {
        var facts = new RunFacts();
        var render = new StreamRender("/tmp/checkout", facts);
        var lines = events.SelectMany(render.Read).ToList();
        return (facts, lines);
    }

    [Fact]
    public void The_session_id_arrives_first_and_is_how_a_run_is_rejoined()
    {
        var (facts, lines) = Play(Fixtures.Init("abc-123"));

        Assert.Equal("abc-123", facts.SessionId);
        Assert.Contains(lines, l => l == "hatch: session abc-123");
        Assert.Contains(lines, l => l.Contains("claude --resume abc-123", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tool_call_is_one_line_naming_the_field_worth_a_line()
    {
        var (_, lines) = Play(Fixtures.ToolUse("Bash", "make test-api"));
        Assert.Contains(lines, l => l == "  ⏺ Bash  make test-api");
    }

    [Fact]
    public void A_path_in_this_tree_is_said_the_way_the_repository_says_it()
    {
        var (_, lines) = Play(
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/tmp/checkout/src/Aerie.Hatch/Claim.cs"}}]}}""");

        Assert.Contains(lines, l => l == "  ⏺ Read  src/Aerie.Hatch/Claim.cs");
    }

    [Fact]
    public void Only_failed_tool_results_are_drawn()
    {
        var (_, quiet) = Play(
            """{"type":"user","message":{"content":[{"type":"tool_result","is_error":false,"content":"fine"}]}}""");
        Assert.Empty(quiet);

        var (_, loud) = Play(
            """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true,"content":[{"text":"it went wrong"}]}]}}""");
        Assert.Contains(loud, l => l == "  ✗ it went wrong");
    }

    [Fact]
    public void Thinking_is_a_pulse_rather_than_a_transcript()
    {
        var (_, lines) = Play(
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":500}""",
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":1200}""",
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":3100}""",
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":3200}""",
            """{"type":"system","subtype":"thinking_tokens","estimated_tokens":6400}""");

        Assert.Equal(["  ✻ thinking… 3k tokens", "  ✻ thinking… 6k tokens"], lines);
    }

    [Fact]
    public void The_result_carries_the_bill_in_Hatch_names_rather_than_the_CLI_ones()
    {
        var (facts, lines) = Play(Fixtures.Result(cost: 2.25m, turns: 31, said: "```work-log\nA title\n\nA summary.\n```"));

        Assert.NotNull(facts.Result);
        Assert.Equal(2.25m, facts.CostUsd);
        Assert.Equal(31, facts.Result.Turns);
        Assert.Equal(65_000, facts.Result.DurationMs);

        var model = Assert.Single(facts.Result.Models!);
        Assert.Equal("claude-opus-5", model.Model);
        Assert.Equal(300, model.CacheCreationTokens);
        Assert.Equal(400, model.CacheReadTokens);
        Assert.Equal(2.25m, model.CostUsd);

        Assert.Contains(lines, l => l.Contains("done in 1m05s, 31 turns, $2.25", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_that_ended_badly_says_so_rather_than_saying_done()
    {
        var (_, lines) = Play(Fixtures.Result(error: true));
        Assert.Contains(lines, l => l.Contains("ended with an error", StringComparison.Ordinal));
    }

    // ---- The block a session is asked to end with ----

    [Fact]
    public void The_first_non_empty_line_is_the_title_and_the_rest_is_the_summary()
    {
        var (title, summary) = StreamRender.WorkLog("""
            Some prose first.

            ```work-log

            The runner claims, heartbeats and lets go

            It does the thing, and then the other thing.
            ```
            """);

        Assert.Equal("The runner claims, heartbeats and lets go", title);
        Assert.Equal("It does the thing, and then the other thing.", summary);
    }

    [Fact]
    public void The_last_block_wins_so_quoting_the_format_costs_nothing()
    {
        var (title, _) = StreamRender.WorkLog("""
            I was asked to end with:

            ```work-log
            A title naming what this session did

            The summary, under 100 words.
            ```

            ...and here is mine:

            ```work-log
            What I actually did
            ```
            """);

        Assert.Equal("What I actually did", title);
    }

    [Fact]
    public void A_session_that_writes_a_title_label_does_not_keep_it()
    {
        var (title, summary) = StreamRender.WorkLog("```work-log\nTitle: Did the thing\n```");

        Assert.Equal("Did the thing", title);

        // A block with a title and nothing under it is a row with no summary,
        // which is a mark the page draws rather than an entry lost.
        Assert.Null(summary);
    }

    [Fact]
    public void No_block_and_an_empty_one_both_leave_the_row_undescribed()
    {
        Assert.Equal((null, null), StreamRender.WorkLog("It went fine, thanks."));
        Assert.Equal((null, null), StreamRender.WorkLog("```work-log\n\n\n```"));
    }

    [Fact]
    public void Anything_that_is_not_an_event_is_stepped_over()
    {
        // The CLI is entitled to say something on stdout that is not an event -
        // a warning, an update notice - and a log that ended on one would be a
        // log lost for a line nobody needed.
        var (facts, lines) = Play("npm notice: a new version is available", "{not json", Fixtures.Init());

        Assert.Equal("s-1", facts.SessionId);
        Assert.Contains(lines, l => l.StartsWith("hatch: session", StringComparison.Ordinal));
    }
}
