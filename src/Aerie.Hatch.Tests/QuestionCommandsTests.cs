namespace Aerie.Hatch.Tests;

/// <summary>
/// <c>ask</c>, <c>questions</c> and <c>answer</c> - the three halves of a
/// decision that is not an agent's to make.
/// </summary>
public sealed class QuestionCommandsTests
{
    private const string Comments = "/api/hatch/issues/AER-12/comments";

    private static QuestionDto AQuestion(
        long id = 1, string key = "AER-12", string body = "Which way?",
        IReadOnlyList<QuestionOptionDto>? options = null) =>
        new(id, key, "A ticket", body, "aerie-hatch", DateTimeOffset.UnixEpoch, options, []);

    private static CommentDto Asked(long id = 5, IReadOnlyList<QuestionOptionDto>? options = null) =>
        new(id, "aerie-hatch", "Which way?", "question", null, options, DateTimeOffset.UnixEpoch);

    // ---- ask ----

    [Fact]
    public async Task A_question_in_prose_is_posted_as_a_question_with_no_options()
    {
        using var h = new CliHarness();
        h.Wire.Json("POST", Comments, Asked());

        Assert.Equal(0, await new QuestionCommands(h.Cli).AskAsync(["AER-12", "Which way?"], default));

        var sent = h.Wire.To("POST", Comments).Single().Read<CommentCreateRequest>();
        Assert.Equal("Which way?", sent.Body);
        Assert.Equal("question", sent.Kind);
        Assert.Null(sent.Options);
    }

    [Fact]
    public async Task Options_are_carried_in_order_with_the_recommended_one_marked()
    {
        using var h = new CliHarness();
        h.Wire.Json("POST", Comments, Asked());

        await new QuestionCommands(h.Cli).AskAsync(
            [
                "AER-12", "How should retries be scoped?",
                "--recommend", "Per-node: one budget each, so a slow node cannot starve the rest",
                "--option", "Global: one budget for the drain, simpler to reason about",
            ],
            default);

        var sent = h.Wire.To("POST", Comments).Single().Read<CommentCreateRequest>();
        Assert.Equal(2, sent.Options!.Count);

        Assert.Equal("Per-node", sent.Options[0].Label);
        Assert.Equal("one budget each, so a slow node cannot starve the rest", sent.Options[0].Detail);
        Assert.True(sent.Options[0].Recommended);

        Assert.Equal("Global", sent.Options[1].Label);
        Assert.False(sent.Options[1].Recommended);
    }

    /// <summary>
    /// Split on the first <c>": "</c> so that a detail may contain colons, which
    /// prose does constantly.
    /// </summary>
    [Fact]
    public void An_option_splits_on_its_first_separator_and_keeps_every_later_colon()
    {
        var option = QuestionCommands.Option("Per-node: one each: no starving, no sharing", recommended: false);

        Assert.Equal("Per-node", option.Label);
        Assert.Equal("one each: no starving, no sharing", option.Detail);
    }

    /// <summary>A spec with no separator at all is a bare label, for a choice that explains itself.</summary>
    [Fact]
    public void An_option_with_no_separator_is_a_label_and_no_detail()
    {
        var option = QuestionCommands.Option("child-weighted", recommended: true);

        Assert.Equal("child-weighted", option.Label);
        Assert.Null(option.Detail);
        Assert.True(option.Recommended);
    }

    /// <summary>
    /// Asking is a full stop: the sentence after the post says the ticket will
    /// not be dispatched again, which is the thing an agent needs to know before
    /// it decides whether to carry on building.
    /// </summary>
    [Fact]
    public async Task Ask_says_the_ticket_is_now_blocked_and_where_to_answer_it()
    {
        using var h = new CliHarness();
        h.Wire.Json("POST", Comments, Asked(id: 9));

        await new QuestionCommands(h.Cli).AskAsync(["AER-12", "Which way?"], default);

        Assert.Contains("asked question #9 on AER-12 as aerie-hatch", h.Said);
        Assert.Contains("AER-12 will not be dispatched again until it is answered:", h.Said);
        Assert.Contains("hatch answer AER-12", h.Said);
        Assert.Contains("https://hatch.example/issues/AER-12", h.Said);
        Assert.DoesNotContain("scripts/hatch.sh", h.Said);
    }

    [Fact]
    public async Task Two_questions_in_one_call_is_refused_because_a_question_is_a_row()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new QuestionCommands(h.Cli).AskAsync(["AER-12", "One?", "Two?"], default));
        Assert.Contains("ask takes one issue and one question - put the choices in --option", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task A_flag_ask_does_not_know_is_refused_by_name()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new QuestionCommands(h.Cli).AskAsync(["AER-12", "Which?", "--urgent"], default));
        Assert.Contains("ask does not take --urgent", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    [Fact]
    public async Task An_option_with_nothing_after_it_is_refused()
    {
        using var h = new CliHarness();

        Assert.Equal(1, await new QuestionCommands(h.Cli).AskAsync(["AER-12", "Which?", "--option"], default));
        Assert.Contains("--option needs \"Label: what it means\"", h.Complained);
    }

    // ---- questions ----

    [Fact]
    public async Task Nothing_open_is_a_sentence_naming_the_ticket_if_one_was_named()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/issues/AER-12/questions", Array.Empty<QuestionDto>());

        Assert.Equal(0, await new QuestionCommands(h.Cli).QuestionsAsync(["AER-12"], default));
        Assert.Equal("hatch: nothing is waiting on an answer on AER-12", h.Said);
    }

    [Fact]
    public async Task With_no_key_it_asks_for_the_whole_house()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/questions", Array.Empty<QuestionDto>());

        await new QuestionCommands(h.Cli).QuestionsAsync([], default);
        Assert.Equal("hatch: nothing is waiting on an answer", h.Said);
        Assert.Equal(1, h.Wire.Count("GET", "/api/hatch/questions"));
    }

    [Fact]
    public async Task Open_questions_are_numbered_and_the_recommendation_is_marked()
    {
        using var h = new CliHarness();
        h.Wire.Json("GET", "/api/hatch/questions", new[]
        {
            AQuestion(options:
            [
                new QuestionOptionDto("Per-node", "one budget each", Recommended: true),
                new QuestionOptionDto("Global", "one budget for the drain"),
            ]),
        });

        await new QuestionCommands(h.Cli).QuestionsAsync([], default);

        Assert.Contains("--- 1 open question(s) ---", h.Said);
        Assert.Contains("[1] Per-node  (recommended)", h.Said);
        Assert.Contains("[2] Global", h.Said);
        Assert.Contains("answer them: hatch answer", h.Said);
    }

    // ---- answer ----

    [Fact]
    public async Task Answer_without_a_terminal_is_refused_before_it_reads_anything()
    {
        using var h = new CliHarness();
        h.In.AtATerminal = false;

        Assert.Equal(1, await new QuestionCommands(h.Cli).AnswerAsync([], default));
        Assert.Contains("answer reads your replies and needs a terminal", h.Complained);
        Assert.Empty(h.Wire.Calls);
    }

    /// <summary>
    /// What gets written is the option's label, so the thread reads as the
    /// decision it was rather than as an index into a list nobody kept.
    /// </summary>
    [Fact]
    public async Task A_bare_number_in_range_is_answered_as_that_options_label()
    {
        using var h = new CliHarness(replies: ["2"]);
        h.Wire.Json("GET", "/api/hatch/questions", new[]
        {
            AQuestion(options: [new QuestionOptionDto("Per-node"), new QuestionOptionDto("Global")]),
        });
        h.Wire.Json("POST", Comments, Asked());

        Assert.Equal(0, await new QuestionCommands(h.Cli).AnswerAsync([], default));

        var sent = h.Wire.To("POST", Comments).Single().Read<CommentCreateRequest>();
        Assert.Equal("Global", sent.Body);
        Assert.Equal("answer", sent.Kind);
        Assert.Equal(1, sent.AnswersId);
        Assert.Contains("answered 1 of 1", h.Said);
    }

    /// <summary>
    /// A number out of range, or one when nothing was offered, is somebody
    /// typing an answer that happens to be numeric - and second-guessing them
    /// would be worse than taking them literally.
    /// </summary>
    [Theory]
    [InlineData("9")]
    [InlineData("0")]
    public async Task A_number_that_names_no_option_is_taken_literally(string typed)
    {
        using var h = new CliHarness(replies: [typed]);
        h.Wire.Json("GET", "/api/hatch/questions", new[]
        {
            AQuestion(options: [new QuestionOptionDto("Per-node"), new QuestionOptionDto("Global")]),
        });
        h.Wire.Json("POST", Comments, Asked());

        await new QuestionCommands(h.Cli).AnswerAsync([], default);
        Assert.Equal(typed, h.Wire.To("POST", Comments).Single().Read<CommentCreateRequest>().Body);
    }

    [Fact]
    public void A_number_with_nothing_offered_is_the_answer_it_was_typed_as()
    {
        Assert.Equal("2", QuestionCommands.Chosen([], "2"));
    }

    /// <summary>
    /// So a session can be abandoned halfway without anybody having to answer
    /// something badly to get out of it.
    /// </summary>
    [Fact]
    public async Task An_empty_reply_leaves_that_question_open_and_moves_on()
    {
        using var h = new CliHarness(replies: ["", "Because."]);
        h.Wire.Json("GET", "/api/hatch/questions", new[] { AQuestion(1), AQuestion(2) });
        h.Wire.Json("POST", Comments, Asked());

        await new QuestionCommands(h.Cli).AnswerAsync([], default);

        Assert.Contains("left open", h.Said);
        Assert.Equal(1, h.Wire.Count("POST", Comments));
        Assert.Equal(2, h.Wire.To("POST", Comments).Single().Read<CommentCreateRequest>().AnswersId);
        Assert.Contains("answered 1 of 2", h.Said);
    }

    /// <summary>A trailing backslash keeps typing, which is how a paragraph gets in.</summary>
    [Fact]
    public async Task A_backslash_at_the_end_of_a_line_carries_on_to_the_next()
    {
        using var h = new CliHarness(replies: ["Because of the first thing,\\", "and the second."]);
        h.Wire.Json("GET", "/api/hatch/questions", new[] { AQuestion() });
        h.Wire.Json("POST", Comments, Asked());

        await new QuestionCommands(h.Cli).AnswerAsync([], default);

        Assert.Equal(
            "Because of the first thing,\nand the second.",
            h.Wire.To("POST", Comments).Single().Read<CommentCreateRequest>().Body);

        // The line printed back is the first one, not the whole paragraph.
        Assert.Contains("answered: Because of the first thing,", h.Said);
    }

    /// <summary>
    /// ^D is the gesture for "I am done here", and treating it as an empty
    /// answer would silently walk the rest of the list.
    /// </summary>
    [Fact]
    public async Task End_of_input_stops_the_whole_session_rather_than_skipping_one_question()
    {
        using var h = new CliHarness(replies: [null]);
        h.Wire.Json("GET", "/api/hatch/questions", new[] { AQuestion(1), AQuestion(2), AQuestion(3) });

        Assert.Equal(0, await new QuestionCommands(h.Cli).AnswerAsync([], default));

        Assert.Empty(h.Wire.To("POST", Comments));
        Assert.DoesNotContain("left open", h.Said);
        Assert.Contains("answered 0 of 3", h.Said);
    }

    /// <summary>
    /// Every answer goes to the ticket as it is typed, so a loop interrupted at
    /// question four keeps the first three.
    /// </summary>
    [Fact]
    public async Task Each_answer_is_posted_before_the_next_question_is_asked()
    {
        using var h = new CliHarness(replies: ["One.", "Two.", null]);
        h.Wire.Json("GET", "/api/hatch/questions", new[] { AQuestion(1), AQuestion(2), AQuestion(3) });
        h.Wire.Json("POST", Comments, Asked());

        await new QuestionCommands(h.Cli).AnswerAsync([], default);

        var posted = h.Wire.To("POST", Comments).Select(c => c.Read<CommentCreateRequest>()).ToList();
        Assert.Equal(["One.", "Two."], posted.Select(p => p.Body));
        Assert.Equal([1L, 2L], posted.Select(p => p.AnswersId));
        Assert.Contains("answered 2 of 3", h.Said);
    }

    [Fact]
    public async Task Having_answered_something_it_says_the_ticket_can_now_be_worked()
    {
        using var h = new CliHarness(replies: ["Yes."]);
        h.Wire.Json("GET", "/api/hatch/issues/AER-12/questions", new[] { AQuestion() });
        h.Wire.Json("POST", Comments, Asked());

        await new QuestionCommands(h.Cli).AnswerAsync(["AER-12"], default);

        Assert.Contains("can be worked: hatch work AER-12", h.Said);
        Assert.DoesNotContain("scripts/hatch.sh", h.Said);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("questions")]
    [InlineData("answer")]
    public async Task Each_of_them_prints_its_own_usage_for_dash_h(string command)
    {
        using var h = new CliHarness();
        var commands = new QuestionCommands(h.Cli);

        var code = command switch
        {
            "ask" => await commands.AskAsync(["-h"], default),
            "questions" => await commands.QuestionsAsync(["--help"], default),
            _ => await commands.AnswerAsync(["-h"], default),
        };

        Assert.Equal(0, code);
        Assert.StartsWith($"usage: hatch {command}", h.Said);
        Assert.Empty(h.Wire.Calls);
    }
}
