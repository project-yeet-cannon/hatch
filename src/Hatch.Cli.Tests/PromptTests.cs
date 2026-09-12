namespace Hatch.Cli.Tests;

/// <summary>The instruction a session is handed.</summary>
public sealed class PromptTests
{
    [Fact]
    public void The_playbook_comes_first_and_the_ticket_after_it()
    {
        var prompt = Prompt.Compose(Fixtures.Work("AER-12"));

        Assert.StartsWith("Do the thing.", prompt, StringComparison.Ordinal);
        Assert.Contains("AER-12  [task]  A ticket", prompt, StringComparison.Ordinal);
        Assert.Contains("moving:   In Progress -> In Review", prompt, StringComparison.Ordinal);
        Assert.Contains("The brief.", prompt, StringComparison.Ordinal);
        Assert.Contains("AER-12 should be in \"In Review\" when you stop", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ticket_with_no_description_says_that_is_worth_noting()
    {
        var work = Fixtures.Work("AER-12", issue: Fixtures.Issue("AER-12", description: ""));
        Assert.Contains("_No description. That is itself worth noting on the ticket._",
            Prompt.Compose(work), StringComparison.Ordinal);
    }

    [Fact]
    public void Its_children_are_listed_where_it_has_them()
    {
        var work = Fixtures.Work("AER-12", children: [Fixtures.Card("AER-13"), Fixtures.Card("AER-14")]);
        var prompt = Prompt.Compose(work);

        Assert.Contains("## Its children", prompt, StringComparison.Ordinal);
        Assert.Contains("- AER-13  [task]  A child", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Decisions_already_made_are_carried_in_and_open_questions_are_not()
    {
        var work = Fixtures.Work("AER-12", questions:
        [
            Fixtures.Question(1, body: "Which way?", answered: true),
            Fixtures.Question(2, body: "And this one?"),
        ]);

        var prompt = Prompt.Compose(work);

        // The one thing a session must not do is reopen a question somebody has
        // already answered, so the answers travel in the prompt rather than
        // waiting in a comment thread.
        Assert.Contains("## Decisions already made", prompt, StringComparison.Ordinal);
        Assert.Contains("**Asked (hatch):** Which way?", prompt, StringComparison.Ordinal);
        Assert.Contains("**Answered (Nathan):** That way.", prompt, StringComparison.Ordinal);

        // An unanswered one is not a decision, and a dispatch carrying one would
        // not have been sent.
        Assert.DoesNotContain("And this one?", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ticket_with_no_answers_gets_no_decisions_section()
    {
        Assert.DoesNotContain("## Decisions already made",
            Prompt.Compose(Fixtures.Work("AER-12")), StringComparison.Ordinal);
    }

    [Fact]
    public void The_ticket_says_when_it_chose_the_model_rather_than_the_playbook()
    {
        var pinned = Fixtures.Work("AER-12", issue: Fixtures.Issue("AER-12", modelOverride: "opus"));

        Assert.Equal("model from AER-12, not the playbook", Prompt.OverrideLine(pinned, "opus", "high"));

        // The value that won is the playbook's either way - the server has
        // already folded the override in - so a flag naming something else is
        // the flag's, and says nothing about the ticket.
        Assert.Null(Prompt.OverrideLine(pinned, "sonnet", "high"));
        Assert.Null(Prompt.OverrideLine(Fixtures.Work("AER-12"), "opus", "high"));
    }

    [Fact]
    public void Both_overrides_read_as_one_sentence()
    {
        var pinned = Fixtures.Work("AER-12",
            issue: Fixtures.Issue("AER-12", modelOverride: "opus", effortOverride: "xhigh"));

        Assert.Equal("model and effort from AER-12, not the playbook", Prompt.OverrideLine(pinned, "opus", "xhigh"));
    }
}
